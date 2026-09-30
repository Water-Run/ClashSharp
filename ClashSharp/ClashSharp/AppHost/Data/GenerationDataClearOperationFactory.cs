using System;
using System.IO;
using System.Threading;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Owns clear-all intent without constructing retired legacy samplers, logs or profile stores.</summary>
internal sealed class GenerationDataClearOperationFactory(DataGenerationManager generations,
    MutationAdmissionBarrier admission, ControllerCredentialService credentials, AppSettingsService settings,
    RuntimeLifecycleCoordinator shutdown, string applicationDataRoot) : IApplicationDataClearOperationFactory
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDataRoot));
    private readonly DataGenerationPathPolicy _paths = new(applicationDataRoot);

    public IApplicationLifetimeMaintenance Create()
    {
        generations.Execute<SettingsGenerationContext>((_, descriptor) => _paths.ValidateDescriptor(descriptor));
        return new ApplicationDataClearOperation(shutdown.PrepareDataRemovalAsync, ClearHostData, _root,
            () => generations.IsDisposalComplete);
    }

    private void ClearHostData()
    {
        using MutationAdmissionLease lease = admission.AcquireShutdownMaintenance();
        generations.Execute<SettingsGenerationContext>((_, descriptor) =>
        {
            _paths.ValidateDescriptor(descriptor);
            // A failed credential deletion must leave migration input and all data files intact.
            credentials.ClearAdmitted(lease, CancellationToken.None);
            settings.ClearLegacySettingsForRemoval(lease);
        });
    }
}
