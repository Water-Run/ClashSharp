using System.Security.Cryptography;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Security;

namespace ClashSharp.ApplicationModel.Security;

/// <summary>Owns verified initialization and explicit deletion of the process controller credential.</summary>
/// <remarks>Constructors and runtime reads have no storage effects. Only admitted startup and data deletion may write.</remarks>
public sealed class ControllerCredentialService : IControllerCredentialProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly IControllerCredentialStore _store;
    private readonly MutationAdmissionBarrier _admission;
    private string? _secret;
    private bool _disposed;

    /// <summary>Creates an uninitialized credential owner without opening storage.</summary>
    /// <param name="store">Independent private credential slot.</param>
    /// <param name="admission">Process-wide mutation admission.</param>
    public ControllerCredentialService(IControllerCredentialStore store, MutationAdmissionBarrier admission)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    /// <inheritdoc />
    public string GetSecret()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _secret ?? throw new ControllerCredentialException("controller.credential.not_initialized");
        }
    }

    /// <summary>Loads an existing valid credential or creates and independently verifies its durable replacement.</summary>
    /// <param name="lease">Active startup authority retained through verification, including a lost write reply.</param>
    /// <param name="cancellationToken">Cancels before the first storage mutation; verification after a write is mandatory.</param>
    public void InitializeAdmitted(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveLease(lease);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_secret is not null) { return; }
            if (TryRead(out string? stored) && ControllerCredentialPolicy.IsValid(stored))
            {
                _secret = stored;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _admission.EnsureActiveLease(lease);
            string generated = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            bool failed = false;
            try { _store.Write(generated); }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { failed = true; }
            if (!TryRead(out string? verified) || !StringComparer.Ordinal.Equals(generated, verified))
            {
                throw new ControllerCredentialException(failed
                    ? "controller.credential.write_failed" : "controller.credential.verification_failed");
            }

            _secret = verified;
        }
    }

    /// <summary>Invalidates the cached credential and verifies removal after the runtime has been stopped.</summary>
    /// <param name="lease">Active data-deletion authority; ordinary settings resets must never call this method.</param>
    /// <param name="cancellationToken">Cancels before deletion begins; absence must be verified after the attempt.</param>
    public void ClearAdmitted(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveLease(lease);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _admission.EnsureActiveLease(lease);
            _secret = null;
            bool failed = false;
            try { _store.Delete(); }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { failed = true; }
            if (TryRead(out _))
            {
                throw new ControllerCredentialException(failed
                    ? "controller.credential.delete_failed" : "controller.credential.delete_not_verified");
            }
        }
    }

    /// <summary>Retires the process projection without changing durable storage.</summary>
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _secret = null; }
    }

    private bool TryRead(out string? secret)
    {
        try { return _store.TryRead(out secret); }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            throw new ControllerCredentialException("controller.credential.read_failed");
        }
    }
}
