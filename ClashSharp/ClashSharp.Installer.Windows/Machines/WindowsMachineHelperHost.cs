using System.Security.Principal;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperElevationVerifier
{
    void VerifyElevated();
}

internal interface IWindowsMachineHelperAuthorityFactory
{
    Task<IWindowsMachineHelperAuthorityLease> CreateAsync(
        InstallerMachineHelperInvocation bootstrap,
        string targetSid,
        CancellationToken cancellationToken);
}

internal interface IWindowsMachineHelperAuthorityLease : IAsyncDisposable
{
    InstallerMachineHelperAuthoritySession Session { get; }
}

internal sealed class WindowsMachineHelperAuthorityFactory
    : IWindowsMachineHelperAuthorityFactory
{
    private readonly IWindowsMachineHelperAuthorityResourcesFactory _resourcesFactory;

    internal WindowsMachineHelperAuthorityFactory(
        IWindowsMachineHelperAuthorityResourcesFactory resourcesFactory)
    {
        ArgumentNullException.ThrowIfNull(resourcesFactory);
        _resourcesFactory = resourcesFactory;
    }

    public async Task<IWindowsMachineHelperAuthorityLease> CreateAsync(
        InstallerMachineHelperInvocation bootstrap,
        string targetSid,
        CancellationToken cancellationToken)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        cancellationToken.ThrowIfCancellationRequested();
        IWindowsMachineHelperAuthorityResources resources =
            _resourcesFactory.Create(targetSid)
            ?? throw new InstallerProtocolException(
                "installer.machine_helper.authority_resources_missing");
        try
        {
            InstallerMachineHelperAuthoritySession session = await
                InstallerMachineHelperAuthoritySession.CreateAsync(
                    bootstrap,
                    targetSid,
                    resources.TransactionStore,
                    resources.Operations,
                    cancellationToken)
                .ConfigureAwait(false);
            return new WindowsMachineHelperAuthorityLease(session, resources);
        }
        catch
        {
            await resources.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class WindowsMachineHelperAuthorityLease
    : IWindowsMachineHelperAuthorityLease
{
    private IWindowsMachineHelperAuthorityResources? _resources;

    internal WindowsMachineHelperAuthorityLease(
        InstallerMachineHelperAuthoritySession session,
        IWindowsMachineHelperAuthorityResources resources)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(resources);
        Session = session;
        _resources = resources;
    }

    public InstallerMachineHelperAuthoritySession Session { get; }

    public async ValueTask DisposeAsync()
    {
        IWindowsMachineHelperAuthorityResources? resources =
            Interlocked.Exchange(ref _resources, null);
        if (resources is not null)
        {
            await resources.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed record WindowsMachineHelperHostLimits(
    TimeSpan ConnectionTimeout,
    TimeSpan SessionTimeout)
{
    internal static WindowsMachineHelperHostLimits Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(30));

    internal void Validate()
    {
        if (ConnectionTimeout <= TimeSpan.Zero
            || ConnectionTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout));
        }

        if (SessionTimeout <= TimeSpan.Zero
            || SessionTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(SessionTimeout));
        }
    }
}

/// <summary>
/// Authenticates both local process endpoints before creating protected-state authority, then runs
/// one bounded helper command session through the already-authenticated pipe.
/// </summary>
internal sealed class WindowsMachineHelperHost
{
    private readonly string _installerExecutablePath;
    private readonly IWindowsMachineHelperElevationVerifier _elevation;
    private readonly IWindowsInstallerExecutableTrustVerifier _trustVerifier;
    private readonly IWindowsMachineHelperParentProcessVerifier _parentVerifier;
    private readonly IWindowsMachineHelperClientFactory _clientFactory;
    private readonly IWindowsMachineHelperAuthorityFactory _authorityFactory;
    private readonly WindowsMachineHelperHostLimits _limits;

    internal WindowsMachineHelperHost(
        string installerExecutablePath,
        IWindowsMachineHelperElevationVerifier elevation,
        IWindowsInstallerExecutableTrustVerifier trustVerifier,
        IWindowsMachineHelperParentProcessVerifier parentVerifier,
        IWindowsMachineHelperClientFactory clientFactory,
        IWindowsMachineHelperAuthorityFactory authorityFactory,
        WindowsMachineHelperHostLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerExecutablePath);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(trustVerifier);
        ArgumentNullException.ThrowIfNull(parentVerifier);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(authorityFactory);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _installerExecutablePath = installerExecutablePath;
        _elevation = elevation;
        _trustVerifier = trustVerifier;
        _parentVerifier = parentVerifier;
        _clientFactory = clientFactory;
        _authorityFactory = authorityFactory;
        _limits = limits;
    }

    internal static WindowsMachineHelperHost CreateDefault(
        string installerExecutablePath,
        InstallerReleaseManifest manifest,
        ReadOnlyMemory<byte> embeddedManifestBytes) =>
        CreateDefault(
            installerExecutablePath,
            manifest,
            certificateOwnershipStore =>
                WindowsMachineHelperOperationExecutor.CreateDefault(
                    embeddedManifestBytes,
                    certificateOwnershipStore));

    internal static WindowsMachineHelperHost CreateDefault(
        string installerExecutablePath,
        InstallerReleaseManifest manifest,
        Func<IInstallerCertificateOwnershipStore, IInstallerMachineHelperOperationExecutor>
            operationsFactory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        return new WindowsMachineHelperHost(
            installerExecutablePath,
            WindowsMachineHelperElevationVerifier.Instance,
            new WindowsInstallerExecutableTrustVerifier(manifest),
            new WindowsMachineHelperParentProcessVerifier(),
            new WindowsMachineHelperClientFactory(),
            new WindowsMachineHelperAuthorityFactory(
                new WindowsMachineHelperAuthorityResourcesFactory(operationsFactory)),
            WindowsMachineHelperHostLimits.Default);
    }

    internal async Task RunAsync(
        InstallerMachineHelperBootstrap bootstrap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        _elevation.VerifyElevated();

        using CancellationTokenSource connectionDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionDeadline.CancelAfter(_limits.ConnectionTimeout);
        using IWindowsInstallerExecutableTrustLease trustLease = await _trustVerifier
            .VerifyAsync(_installerExecutablePath, connectionDeadline.Token)
            .ConfigureAwait(false);
        using IWindowsMachineHelperParentProcessLease parentLease = _parentVerifier.Acquire(
            bootstrap.ParentProcessId,
            trustLease.ExecutablePath);
        await using IWindowsMachineHelperClient client = _clientFactory.Create(bootstrap);
        await client.ConnectAsync(connectionDeadline.Token).ConfigureAwait(false);
        client.VerifyServer(parentLease.ProcessId);
        parentLease.VerifyAlive();

        using CancellationTokenSource sessionDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sessionDeadline.CancelAfter(_limits.SessionTimeout);
        InstallerMachineHelperCommand firstCommand = await InstallerMachineHelperFraming
            .ReadCommandAsync(client.Transport, sessionDeadline.Token)
            .ConfigureAwait(false);
        InstallerMachineHelperInvocation firstInvocation = firstCommand.ToInvocation();
        if (firstInvocation != bootstrap.Invocation)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.session_bootstrap_mismatch");
        }

        InstallerTransactionSnapshot firstState = firstCommand.ToDurableState();
        if (!string.Equals(
                firstState.Journal.TargetSid,
                parentLease.UserSid,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.target_sid_mismatch");
        }

        await using IWindowsMachineHelperAuthorityLease authority = await _authorityFactory
            .CreateAsync(
                bootstrap.Invocation,
                parentLease.UserSid,
                sessionDeadline.Token)
            .ConfigureAwait(false);
        await InstallerMachineHelperAuthorityLoop
            .RunAsync(
                client.Transport,
                authority.Session,
                firstCommand,
                sessionDeadline.Token)
            .ConfigureAwait(false);
    }
}

internal sealed class WindowsMachineHelperElevationVerifier
    : IWindowsMachineHelperElevationVerifier
{
    internal static WindowsMachineHelperElevationVerifier Instance { get; } = new();

    private WindowsMachineHelperElevationVerifier()
    {
    }

    public void VerifyElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The machine helper is available only on Windows.");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.elevation_required");
        }
    }
}
