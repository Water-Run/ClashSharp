using System;
using System.Threading;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;

namespace ClashSharp.Service;

/// <summary>Connects legacy runtime factories to the primary host's independent credential owner.</summary>
/// <remarks>Binding occurs explicitly after verified startup, never from a constructor or a preferences getter.</remarks>
internal sealed class MihomoControllerCredentials : IControllerCredentialProvider, IAppDataMaintenanceCredentials
{
    public static MihomoControllerCredentials Instance { get; } = new();
    private Binding? _binding;

    public void Bind(ControllerCredentialService credentials, MutationAdmissionBarrier admission)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(admission);
        Binding? existing = Interlocked.CompareExchange(ref _binding, new(credentials, admission), null);
        if (existing is not null && (!ReferenceEquals(existing.Credentials, credentials) || !ReferenceEquals(existing.Admission, admission)))
        {
            throw new InvalidOperationException("Controller credentials are already bound to another primary host.");
        }
    }

    public string GetSecret() => GetBinding().Credentials.GetSecret();

    public void ClearAll(bool useTerminalAdmission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Binding binding = GetBinding();
        using MutationAdmissionLease lease = useTerminalAdmission
            ? binding.Admission.AcquireShutdownMaintenance() : binding.Admission.AcquireOrdinary(cancellationToken);
        binding.Credentials.ClearAdmitted(lease, cancellationToken);
    }

    private Binding GetBinding() => Volatile.Read(ref _binding)
        ?? throw new ControllerCredentialException("controller.credential.not_initialized");

    private sealed record Binding(ControllerCredentialService Credentials, MutationAdmissionBarrier Admission);
}
