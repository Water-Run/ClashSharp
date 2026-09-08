using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Connects the startup coordinator to the sole settings authority and packaged StartupTask.</summary>
internal sealed class StartupSettingsOperationAdapter : IStartupSettingsOperation
{
    private readonly AppSettingsService _settings;
    private readonly StartupLaunchService _startup;

    public StartupSettingsOperationAdapter(AppSettingsService settings, StartupLaunchService startup)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
    }

    public bool ReadPreference() => _settings.LaunchAtStartupEnabled;

    public void WritePreference(bool enabled, MutationAdmissionLease admissionLease) =>
        _settings.WriteAdmitted(admissionLease, editor => editor.LaunchAtStartupEnabled = enabled);

    public async Task<bool?> ReadRegistrationAsync(CancellationToken cancellationToken) =>
        await _startup.TryGetStateAsync(cancellationToken) switch
        {
            StartupLaunchTaskState.Enabled => true,
            StartupLaunchTaskState.Disabled => false,
            _ => null,
        };

    public Task ApplyRegistrationAsync(bool enabled, CancellationToken cancellationToken) =>
        _startup.SetEnabledAsync(enabled, cancellationToken);
}
