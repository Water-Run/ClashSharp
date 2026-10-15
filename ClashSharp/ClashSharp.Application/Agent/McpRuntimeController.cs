using System.Text.Json;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Owns endpoint configuration, protected credential readback, and awaited lifecycle changes.</summary>
public sealed class McpRuntimeController : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ClientJsonOptions = new() { WriteIndented = true };
    private readonly MutationAdmissionBarrier _admission;
    private readonly IMcpCredentialStore _credentials;
    private readonly IMcpServerHost _host;
    private readonly McpAccessState _access;
    private readonly RepositoryOperationLifetime _operations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _retirementGate = new();
    private McpServerConfiguration _configuration = new(McpServerConfiguration.DefaultPort, McpAccessPolicy.Default);
    private string? _error;
    private int _suspended;
    private bool _rotationRequired;
    private Task? _retirement;

    /// <summary>Creates an inactive controller and assumes ownership of the store and host without performing I/O.</summary>
    /// <param name="admission">Shared ordinary/exclusive product-operation barrier.</param>
    /// <param name="credentials">Generation-owned protected credential store.</param>
    /// <param name="host">Generation-owned authenticated listener.</param>
    /// <param name="access">Read-only authentication state supplied to the business backend.</param>
    public McpRuntimeController(MutationAdmissionBarrier admission, IMcpCredentialStore credentials,
        IMcpServerHost host, McpAccessState access)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _operations = new(this);
    }

    /// <summary>Captures listener truth separately from the last applied configuration.</summary>
    /// <returns>A detached projection containing no credential material.</returns>
    public McpRuntimeSnapshot CaptureSnapshot() => new(Volatile.Read(ref _configuration), _host.BoundEndpoint,
        _access.HasCredential, Volatile.Read(ref _suspended) != 0, Volatile.Read(ref _error));

    /// <summary>Reads effective configuration only after independently verifying its access and listener state.</summary>
    /// <returns>The verified effective configuration.</returns>
    public McpServerConfiguration CaptureConfiguration()
    {
        McpServerConfiguration configuration = Volatile.Read(ref _configuration);
        Verify(configuration);
        return configuration;
    }

    /// <summary>Applies a complete user configuration under drained exclusive admission.</summary>
    /// <param name="configuration">Complete target policy and endpoint.</param>
    /// <param name="lease">Exclusive ownership held until application and observation finish.</param>
    /// <param name="cancellationToken">Cancels startup or uncommitted storage; restoration remains owned.</param>
    public async Task ApplyAdmittedAsync(McpServerConfiguration configuration, MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _admission.EnsureActiveExclusiveLease(lease);
        using IDisposable operation = _operations.Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _admission.EnsureActiveExclusiveLease(lease);
            McpServerConfiguration before = _configuration;
            McpAccessCredential? credentialBefore = _access.Credential;
            bool suspendedBefore = _suspended != 0;
            try
            {
                McpAccessCredential? credential = configuration.Access.Enabled
                    ? before.Access.Enabled && !suspendedBefore && credentialBefore is not null
                        ? credentialBefore : await EnsureCredentialAsync(cancellationToken).ConfigureAwait(false)
                    : credentialBefore;
                await InstallAsync(configuration, credential, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _configuration, configuration);
                Volatile.Write(ref _suspended, 0);
                Volatile.Write(ref _error, null);
                Verify(configuration);
            }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
            {
                Volatile.Write(ref _error, "mcp.configuration_failed");
                // A failed port change or stop must never restore grants the user just revoked.
                McpAccessPolicy restricted = new(before.Access.Enabled && configuration.Access.Enabled,
                    before.Access.Scopes & configuration.Access.Scopes);
                McpServerConfiguration recovery = new(before.Port, restricted);
                try
                {
                    if (suspendedBefore)
                    {
                        _access.Publish(new(false, restricted.Scopes), credentialBefore);
                        await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    else { await InstallAsync(recovery, credentialBefore, CancellationToken.None).ConfigureAwait(false); }
                    Volatile.Write(ref _configuration, recovery);
                    Volatile.Write(ref _suspended, suspendedBefore ? 1 : 0);
                }
                catch (Exception restoration) when (!ExceptionGraphClassifier.IsProcessFatal(restoration))
                {
                    _access.Publish(new(false, restricted.Scopes), null);
                    throw new AggregateException("MCP configuration and restoration failed.", failure, restoration);
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Regenerates the persisted credential and suspends access when its replacement cannot be confirmed.</summary>
    /// <param name="lease">Exclusive ownership excluding every authenticated product operation.</param>
    /// <param name="cancellationToken">Cancels before the new credential is written.</param>
    public async Task RotateCredentialAdmittedAsync(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(lease);
        using IDisposable operation = _operations.Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _admission.EnsureActiveExclusiveLease(lease);
            cancellationToken.ThrowIfCancellationRequested();
            _access.Publish(new(false, _configuration.Access.Scopes), null);
            try
            {
                string secret = McpAccessCredential.CreateSecret();
                McpAccessCredential verified = await WriteCredentialAsync(secret).ConfigureAwait(false);
                await InstallAsync(_configuration, verified, CancellationToken.None).ConfigureAwait(false);
                _rotationRequired = false;
                Volatile.Write(ref _suspended, 0);
                Volatile.Write(ref _error, null);
            }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
            {
                _access.Publish(new(false, _configuration.Access.Scopes), null);
                _rotationRequired = true;
                Volatile.Write(ref _suspended, 1);
                Volatile.Write(ref _error, "mcp.credential_rotation_failed");
                try { await _host.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception stopping) when (!ExceptionGraphClassifier.IsProcessFatal(stopping))
                {
                    throw new AggregateException("MCP credential rotation and shutdown failed.", failure, stopping);
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Creates an explicit user-requested client configuration containing the protected bearer secret.</summary>
    /// <remarks>This result is for a user copy action only; it must never be exposed as an MCP tool or logged.</remarks>
    /// <param name="lease">Exclusive ownership for first-use credential creation and readback.</param>
    /// <param name="cancellationToken">Cancels uncommitted credential preparation.</param>
    /// <returns>A generic HTTP connection object with endpoint and authorization header.</returns>
    public async Task<string> GetClientConfigurationAdmittedAsync(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(lease);
        using IDisposable operation = _operations.Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _admission.EnsureActiveExclusiveLease(lease);
            McpAccessCredential credential = await EnsureCredentialAsync(cancellationToken).ConfigureAwait(false);
            string? secret = await _credentials.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!credential.TryAuthenticate(secret, out _)) { throw new InvalidDataException("MCP credential readback changed."); }
            _access.Publish(_access.Policy, credential);
            return JsonSerializer.Serialize(new
            {
                url = _configuration.Endpoint.AbsoluteUri,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret },
            }, ClientJsonOptions);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Pauses the listener for its owning lifecycle without rewriting user configuration.</summary>
    /// <param name="cancellationToken">Cancels waiting; the host retains any started stop operation.</param>
    public async Task SuspendAsync(CancellationToken cancellationToken)
    {
        using IDisposable operation = _operations.Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SuspendCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Resumes a lifecycle-paused listener using its last applied configuration.</summary>
    /// <param name="cancellationToken">Cancels startup before publication.</param>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        using IDisposable operation = _operations.Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            McpAccessCredential? credential = _configuration.Access.Enabled
                ? await EnsureCredentialAsync(cancellationToken).ConfigureAwait(false) : _access.Credential;
            await InstallAsync(_configuration, credential, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _suspended, 0);
            Volatile.Write(ref _error, null);
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            _access.Publish(new(false, _configuration.Access.Scopes), null);
            Volatile.Write(ref _suspended, 1);
            Volatile.Write(ref _error, "mcp.resume_failed");
            try { await _host.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception stopping) when (!ExceptionGraphClassifier.IsProcessFatal(stopping))
            {
                throw new AggregateException("MCP resume and shutdown failed.", failure, stopping);
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_retirementGate) { return new(_retirement ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        await _operations.DisposeAsync().ConfigureAwait(false);
        _access.Publish(new(false, _configuration.Access.Scopes), null);
        List<Exception> failures = [];
        try { await _host.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { failures.Add(failure); }
        try { await _credentials.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { failures.Add(failure); }
        _gate.Dispose();
        if (failures.Count == 1) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException("MCP runtime retirement failed.", failures); }
    }

    private async Task SuspendCoreAsync(CancellationToken cancellationToken)
    {
        _access.Publish(new(false, _configuration.Access.Scopes), _access.Credential);
        Volatile.Write(ref _suspended, 1);
        await _host.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallAsync(McpServerConfiguration configuration, McpAccessCredential? credential, CancellationToken cancellationToken)
    {
        if (_host.BoundEndpoint is not null && _host.BoundEndpoint != configuration.Endpoint)
        {
            await _host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        _access.Publish(configuration.Access, credential);
        if (configuration.Access.Enabled)
        {
            if (credential is null) { throw new InvalidOperationException("MCP credentials are unavailable."); }
            await _host.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        else { await _host.StopAsync(cancellationToken).ConfigureAwait(false); }
        Verify(configuration);
    }

    private void Verify(McpServerConfiguration configuration)
    {
        Uri? expected = configuration.Access.Enabled ? configuration.Endpoint : null;
        if (_access.Policy != configuration.Access || _host.BoundEndpoint != expected
            || configuration.Access.Enabled && !_access.HasCredential)
        {
            throw new InvalidOperationException("The effective MCP configuration could not be verified.");
        }
    }

    private async Task<McpAccessCredential> EnsureCredentialAsync(CancellationToken cancellationToken)
    {
        if (_rotationRequired) { throw new InvalidOperationException("MCP credential regeneration must be retried before access resumes."); }
        string? secret = await _credentials.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            if (_access.HasCredential) { throw new InvalidDataException("The persisted MCP credential disappeared."); }
            secret = McpAccessCredential.CreateSecret();
            await _credentials.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
            return await ReadWrittenCredentialAsync(secret, cancellationToken).ConfigureAwait(false);
        }
        McpAccessCredential credential = new(secret);
        if (_access.Credential is { } current && current.Identity != credential.Identity)
        {
            throw new InvalidDataException("The persisted MCP credential changed unexpectedly.");
        }
        return credential;
    }

    private async Task<McpAccessCredential> WriteCredentialAsync(string secret)
    {
        Exception? writeFailure = null;
        try { await _credentials.WriteAsync(secret, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { writeFailure = failure; }
        try { return await ReadWrittenCredentialAsync(secret, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception confirmation) when (writeFailure is not null && !ExceptionGraphClassifier.IsProcessFatal(confirmation))
        {
            throw new AggregateException("The MCP credential replacement could not be confirmed.", writeFailure, confirmation);
        }
    }

    private async Task<McpAccessCredential> ReadWrittenCredentialAsync(string expected, CancellationToken cancellationToken)
    {
        string? observed = await _credentials.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (observed is null) { throw new InvalidDataException("The MCP credential write could not be verified."); }
        McpAccessCredential credential = new(observed);
        if (!credential.TryAuthenticate(expected, out _)) { throw new InvalidDataException("The MCP credential write could not be verified."); }
        return credential;
    }
}
