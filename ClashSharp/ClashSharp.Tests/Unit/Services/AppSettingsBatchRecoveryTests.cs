using System.Collections;
using System.Diagnostics.CodeAnalysis;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Exercises actual settings batches against an isolated store with uncertain writes.</summary>
public sealed class AppSettingsBatchRecoveryTests
{
    private static readonly string[] UrlKeys =
    [
        nameof(AppSettingsService.ConnectionTestProxyUrl1),
        nameof(AppSettingsService.ConnectionTestProxyUrl2),
        nameof(AppSettingsService.ConnectionTestDirectUrl),
    ];

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Write_WhenStoreChangesThenThrows_RestoresEveryAttemptedValue(int failedIndex, bool hadPreviousValues)
    {
        FaultingStore store = CreateUrlStore(hadPreviousValues);
        KeyValuePair<string, object>[] baseline = Snapshot(store);
        AppSettingsService settings = new(store);
        List<AppSettingChangedEventArgs> observed = [];
        settings.SettingChanged += (_, change) => observed.Add(change);
        IOException failure = new("write acknowledgment lost");
        bool failed = false;
        store.OnWrite = (key, _, after) =>
        {
            if (!failed && after && key == UrlKeys[failedIndex])
            {
                failed = true;
                throw failure;
            }
        };

        Assert.Same(failure, Assert.Throws<IOException>(() => settings.SetConnectionTestUrls(
            "new-one.example.test", "new-two.example.test", "new-direct.example.test")));

        Assert.Equal(baseline, Snapshot(store));
        Assert.Empty(observed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Write_WhenStoreRejectsBeforeChanging_RestoresEarlierValues(int failedIndex)
    {
        FaultingStore store = CreateUrlStore(hadPreviousValues: true);
        KeyValuePair<string, object>[] baseline = Snapshot(store);
        AppSettingsService settings = new(store);
        IOException failure = new("write rejected");
        bool failed = false;
        store.OnWrite = (key, _, after) =>
        {
            if (!failed && !after && key == UrlKeys[failedIndex])
            {
                failed = true;
                throw failure;
            }
        };

        Assert.Same(failure, Assert.Throws<IOException>(() => settings.SetConnectionTestUrls(
            "new-one.example.test", "new-two.example.test", "new-direct.example.test")));

        Assert.Equal(baseline, Snapshot(store));
    }

    [Theory]
    [InlineData(nameof(AppSettingsService.DisplayLanguage))]
    [InlineData(nameof(AppSettingsService.AppThemeMode))]
    [InlineData(nameof(AppSettingsService.AppAccentColorMode))]
    public void Reset_WhenRemovalChangesThenThrows_RestoresValuesWithoutPublishingDefaults(string failedKey)
    {
        FaultingStore store = new()
        {
            [nameof(AppSettingsService.DisplayLanguage)] = (int)AppLanguage.German,
            [nameof(AppSettingsService.AppThemeMode)] = (int)AppThemeMode.Dark,
            [nameof(AppSettingsService.AppAccentColorMode)] = (int)AppAccentColorMode.Custom,
            ["UnrelatedOwner"] = "retained",
        };
        KeyValuePair<string, object>[] baseline = Snapshot(store);
        AppSettingsService settings = new(store);
        List<AppSettingChangedEventArgs> observed = [];
        settings.SettingChanged += (_, change) => observed.Add(change);
        IOException failure = new("remove acknowledgment lost");
        bool failed = false;
        store.OnWrite = (key, value, after) =>
        {
            if (!failed && after && value is null && key == failedKey)
            {
                failed = true;
                throw failure;
            }
        };

        Assert.Same(failure, Assert.Throws<IOException>(settings.ResetAllSettings));

        Assert.Equal(baseline, Snapshot(store));
        Assert.Empty(observed);
    }

    [Fact]
    public void Write_WhenSeveralRestorationsFail_AttemptsTheRemainingKeysAndPreservesEveryFailure()
    {
        FaultingStore store = CreateUrlStore(hadPreviousValues: true);
        AppSettingsService settings = new(store);
        IOException applyFailure = new("write acknowledgment lost");
        IOException directRollbackFailure = new("direct URL restoration rejected");
        IOException proxyRollbackFailure = new("proxy URL restoration rejected");
        List<string> restorationAttempts = [];
        List<AppSettingChangedEventArgs> observed = [];
        settings.SettingChanged += (_, change) => observed.Add(change);
        bool applying = true;
        store.OnWrite = (key, _, after) =>
        {
            if (applying && after && key == UrlKeys[2])
            {
                applying = false;
                throw applyFailure;
            }
            if (!applying && !after)
            {
                restorationAttempts.Add(key);
                if (key == UrlKeys[2]) { throw directRollbackFailure; }
                if (key == UrlKeys[1]) { throw proxyRollbackFailure; }
            }
        };

        AggregateException failure = Assert.Throws<AggregateException>(() => settings.SetConnectionTestUrls(
            "new-one.example.test", "new-two.example.test", "new-direct.example.test"));

        Assert.Equal([applyFailure, directRollbackFailure, proxyRollbackFailure], failure.InnerExceptions);
        Assert.Equal(UrlKeys.Reverse(), restorationAttempts);
        Assert.Equal("https://old-one.example.test", settings.ConnectionTestProxyUrl1);
        Assert.Equal("https://new-two.example.test", settings.ConnectionTestProxyUrl2);
        Assert.Equal("https://new-direct.example.test", settings.ConnectionTestDirectUrl);
        Assert.Empty(observed);
    }

    [Fact]
    public async Task ApplyChanges_WhenPageCancelsAfterAnUncertainWrite_RetainsAdmissionThroughRollback()
    {
        FaultingStore store = CreateUrlStore(hadPreviousValues: true);
        KeyValuePair<string, object>[] baseline = Snapshot(store);
        AppSettingsService settings = new(store);
        MutationAdmissionBarrier admission = new();
        settings.ConfigureMutationAdmission(admission);
        using CancellationTokenSource cancellation = new();
        ValueTask<MutationAdmissionLease> exclusive = default;
        IOException failure = new("write acknowledgment lost");
        bool failed = false;
        int restorationAttempts = 0;
        store.OnWrite = (key, _, after) =>
        {
            if (!failed && after && key == UrlKeys[1])
            {
                failed = true;
                cancellation.Cancel();
                exclusive = admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
                Assert.False(exclusive.IsCompleted);
                throw failure;
            }
            if (failed && !after)
            {
                restorationAttempts++;
                Assert.False(exclusive.IsCompleted);
                Assert.Throws<MutationAdmissionRejectedException>(() => admission.AcquireOrdinary());
            }
        };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => settings.ApplyChangesAsync(
            [Change(SettingsRegistry.Keys.ConnectionTestProxyUrl1, "new-one.example.test"),
             Change(SettingsRegistry.Keys.ConnectionTestProxyUrl2, "new-two.example.test")], cancellation.Token)));

        await using MutationAdmissionLease lease = await exclusive;
        Assert.True(lease.IsExclusive);
        Assert.Equal(2, restorationAttempts);
        Assert.Equal(baseline, Snapshot(store));
    }

    [Fact]
    public async Task ResetPreferenceGroup_WhenAliasRemovalFails_RestoresTheCompleteRegionalSelection()
    {
        FaultingStore store = new()
        {
            [nameof(AppSettingsService.MainlandChinaFeatureMode)] = (int)MainlandChinaFeatureMode.Disabled,
            [nameof(AppSettingsService.MainlandChinaDisplayEnabled)] = false,
            [nameof(AppSettingsService.MainlandChinaUrlBlockingEnabled)] = true,
        };
        KeyValuePair<string, object>[] baseline = Snapshot(store);
        AppSettingsService settings = new(store);
        IOException failure = new("legacy alias removal acknowledgment lost");
        bool failed = false;
        store.OnWrite = (key, value, after) =>
        {
            if (!failed && after && value is null && key == nameof(AppSettingsService.MainlandChinaDisplayEnabled))
            {
                failed = true;
                throw failure;
            }
        };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => settings.ResetPreferenceGroupAsync(
            SettingsResetScope.MainlandChina, CancellationToken.None)));

        Assert.Equal(baseline, Snapshot(store));
        Assert.Equal(MainlandChinaFeatureMode.Disabled, settings.MainlandChinaFeatureMode);
        Assert.False(settings.MainlandChinaDisplayEnabled);
        Assert.True(settings.MainlandChinaUrlBlockingEnabled);
    }

    private static SettingValueChange Change(SettingKey key, string value) =>
        new(key, SettingsRegistry.Default.Get(key.Value).NormalizeValue(value).Value!);

    private static KeyValuePair<string, object>[] Snapshot(FaultingStore store) => store.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();

    private static FaultingStore CreateUrlStore(bool hadPreviousValues)
    {
        FaultingStore store = new() { ["UnrelatedOwner"] = "retained" };
        if (hadPreviousValues)
        {
            store[UrlKeys[0]] = "https://old-one.example.test";
            store[UrlKeys[1]] = "https://old-two.example.test";
            store[UrlKeys[2]] = "https://old-direct.example.test";
        }
        return store;
    }

    private sealed class FaultingStore : IDictionary<string, object>
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
        public Action<string, object?, bool>? OnWrite { get; set; }

        public object this[string key]
        {
            get => _values[key];
            set
            {
                OnWrite?.Invoke(key, value, false);
                _values[key] = value;
                OnWrite?.Invoke(key, value, true);
            }
        }

        public ICollection<string> Keys => _values.Keys;
        public ICollection<object> Values => _values.Values;
        public int Count => _values.Count;
        public bool IsReadOnly => false;
        public void Add(string key, object value) => _values.Add(key, value);
        public void Add(KeyValuePair<string, object> item) => Add(item.Key, item.Value);
        public void Clear() => _values.Clear();
        public bool Contains(KeyValuePair<string, object> item) => ((ICollection<KeyValuePair<string, object>>)_values).Contains(item);
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, object>>)_values).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(KeyValuePair<string, object> item) => Contains(item) && Remove(item.Key);
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out object value) => _values.TryGetValue(key, out value);

        public bool Remove(string key)
        {
            OnWrite?.Invoke(key, null, false);
            bool removed = _values.Remove(key);
            OnWrite?.Invoke(key, null, true);
            return removed;
        }
    }
}
