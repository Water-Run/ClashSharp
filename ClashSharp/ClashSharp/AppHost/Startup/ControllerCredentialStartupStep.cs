using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Verifies private credentials after the Installer gate and before runtime recovery can generate configuration.</summary>
internal sealed class ControllerCredentialStartupStep(
    ControllerCredentialService credentials,
    MutationAdmissionBarrier admission,
    MihomoControllerCredentials binding) : IStartupStep
{
    public string Name => "controller-credential";

    public int Order => 140;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using MutationAdmissionLease lease = admission.AcquireOrdinary(cancellationToken);
            credentials.InitializeAdmitted(lease, cancellationToken);
            binding.Bind(credentials, admission);
            return Task.FromResult(StartupStepResult.Succeeded());
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        { throw; }
        catch (ControllerCredentialException exception) { return Task.FromResult(StartupStepResult.Fatal(exception.Code)); }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return Task.FromResult(StartupStepResult.Fatal("controller.credential.startup_failed"));
        }
    }
}
