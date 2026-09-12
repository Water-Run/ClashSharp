using System;
using System.Threading;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Triggers;

namespace ClashSharp.Hosting.Settings;

/// <summary>Shares one installed generation configuration between scheduling and fired-notification delivery.</summary>
internal sealed class TriggerSettingsState : ITriggerSchedulerSettings
{
    private Configuration? _installed = new(false, true);

    public TriggerSettingsState(DataGenerationDescriptor generation) =>
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));

    public DataGenerationDescriptor Generation { get; }
    public bool IsEnabled => Read().Enabled;
    public bool NotificationsEnabled => Read().NotificationsEnabled;

    internal Configuration Read() => Volatile.Read(ref _installed)
        ?? throw new ObjectDisposedException(nameof(TriggerSettingsState));

    internal void Install(Configuration configuration)
    {
        _ = Read();
        Volatile.Write(ref _installed, configuration);
    }

    internal void Retire() => Volatile.Write(ref _installed, null);

    internal sealed record Configuration(bool Enabled, bool NotificationsEnabled);
}
