using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Service;

public sealed partial class AppSettingsService
{
    private GenerationSettingsAuthority? _authority;
    private SettingsAuthoritySnapshot? _publishedAuthority;

    /// <summary>Ends legacy writes and binds existing read-only consumers to the verified process authority.</summary>
    internal void BindAuthority(GenerationSettingsAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_syncLock)
        {
            if (_authority is not null && !ReferenceEquals(_authority, authority))
            {
                throw new InvalidOperationException("Application settings are already bound to another process authority.");
            }
            SettingsAuthoritySnapshot snapshot = authority.CaptureSnapshot();
            if (_authority is null) { authority.StateChanged += PublishAuthoritySnapshot; }
            _publishedAuthority = snapshot;
            Volatile.Write(ref _authority, authority);
        }
    }

    private void PublishAuthoritySnapshot(SettingsAuthoritySnapshot snapshot)
    {
        List<AppSettingChangedEventArgs> changed = [];
        lock (_syncLock)
        {
            SettingsAuthoritySnapshot? previous = _publishedAuthority;
            _publishedAuthority = snapshot;
            foreach ((SettingKey key, SettingDesiredEntry next) in snapshot.Envelope.Desired)
            {
                object? before = previous is not null && previous.Envelope.Desired.TryGetValue(key, out SettingDesiredEntry? prior)
                    ? ToLegacyValue(prior.Value) : null;
                object after = ToLegacyValue(next.Value);
                if (!Equals(before, after)) { changed.Add(new(key.Value, before, after, wasRemoved: false)); }
            }
        }
        NotifySettingChanges(changed);
    }

    private object ReadAuthorityValue(GenerationSettingsAuthority authority, string key)
    {
        string canonical = key == KeyConnectionTestUrl ? SettingsRegistry.Keys.ConnectionTestProxyUrl1.Value : key;
        if (key == KeyMainlandChinaDisplayEnabled)
        {
            return authority.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.MainlandChinaFeatureMode].Value
                .Get<ClashSharp.Model.MainlandChinaFeatureMode>() != ClashSharp.Model.MainlandChinaFeatureMode.Disabled;
        }
        return ToLegacyValue(authority.CaptureSnapshot().Envelope.Desired[new SettingKey(canonical)].Value);
    }

    private static object ToLegacyValue(SettingValue value) => value.ValueType == typeof(bool) ? value.Get<bool>()
        : value.ValueType == typeof(int) ? value.Get<int>()
        : value.ValueType == typeof(string) ? value.Get<string>()
        : Convert.ToInt32(Enum.Parse(value.ValueType, value.CanonicalText), CultureInfo.InvariantCulture);

    private static async Task ApplyAuthorityChangesAsync(GenerationSettingsAuthority authority,
        IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken)
    {
        SettingsAuthorityResult result = await authority.ApplyChangesAsync(changes, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (!result.IsSucceeded && result.Status != SettingsAuthorityStatus.DeferredToRestart)
        {
            throw new SettingsActionFailedException(result.Status, result.Code ?? "settings.action.failed");
        }
    }
}
