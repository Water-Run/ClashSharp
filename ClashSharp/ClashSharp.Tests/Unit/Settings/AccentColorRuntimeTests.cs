using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies complete resource observation, unavailable resources and interrupted accent writes.</summary>
public sealed class AccentColorRuntimeTests
{
    private static readonly AccentColorConfiguration Blue = new(AppAccentColorMode.Custom, "#FF0078D4");
    private static readonly AccentColorConfiguration Purple = new(AppAccentColorMode.Custom, "#804477AA");
    private static readonly AccentColorConfiguration System = new(AppAccentColorMode.FollowSystem, "#FF0078D4");

    [Fact]
    public void Construction_DoesNotOpenResourcesOrWriteDefaults()
    {
        Store store = new() { BeforeRead = () => throw new InvalidOperationException("No application.") };
        AccentColorRuntime runtime = Create(store);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Throws<InvalidOperationException>(() => runtime.CaptureConfiguration());
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public void UnavailableResources_RejectTheRequestBeforeAnyWriteOrFalseAcknowledgement()
    {
        Store store = new() { BeforeRead = () => throw new InvalidOperationException("No application.") };
        AccentColorRuntime runtime = Create(store);
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(Purple));
        Assert.Equal(0, store.Writes);
        store.BeforeRead = null;
        Assert.Equal(System, runtime.CaptureConfiguration());
    }

    [Fact]
    public void CompletePalette_IsIndependentlyObservedIncludingResourceKindAndColorAlpha()
    {
        Store store = new();
        AccentColorRuntime runtime = Create(store);
        runtime.Apply(Purple);
        Assert.Equal(Purple, runtime.CaptureConfiguration());
        Assert.Equal(new AccentResourceValue(0x804477AA, false), store.Values["color"]);
        Assert.Equal(new AccentResourceValue(0x804477AA, true), store.Values["brush"]);
        Assert.Equal(2, store.Writes);
        Assert.Equal(3, store.Reads);
        store.Values["brush"] = new(0x804477AA, false);
        Assert.Throws<InvalidOperationException>(() => runtime.CaptureConfiguration());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void InterruptedWrite_ObservesTheOldPaletteOrUnknownInsteadOfClaimingSuccess(int failAtWrite, bool oldStillIntact)
    {
        Store store = new();
        AccentColorRuntime runtime = Create(store);
        runtime.Apply(Blue);
        int priorWrites = store.Writes;
        IOException failure = new("Isolated resource write failure.");
        store.BeforeWrite = () => { if (store.Writes == priorWrites + failAtWrite) { throw failure; } };
        Assert.Same(failure, Assert.Throws<IOException>(() => runtime.Apply(Purple)));
        if (oldStillIntact) { Assert.Equal(Blue, runtime.CaptureConfiguration()); }
        else { Assert.Throws<InvalidOperationException>(() => runtime.CaptureConfiguration()); }
        store.BeforeWrite = null;
        runtime.Apply(Purple);
        Assert.Equal(Purple, runtime.CaptureConfiguration());
    }

    [Fact]
    public void LostLastWriteReply_IsResolvedFromTheCompleteActualPalette()
    {
        Store store = new();
        AccentColorRuntime runtime = Create(store);
        store.AfterWrite = () => { if (store.Writes == 2) { throw new IOException("Isolated lost reply."); } };
        runtime.Apply(Purple);
        Assert.Equal(Purple, runtime.CaptureConfiguration());
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public void FailedFinalObservation_RetainsOnlyAnAttemptUntilResourcesCanBeReadAgain()
    {
        Store store = new();
        AccentColorRuntime runtime = Create(store);
        store.BeforeRead = () => { if (store.Reads == 2) { throw new IOException("Isolated observation failure."); } };
        Assert.Throws<IOException>(() => runtime.Apply(Purple));
        Assert.Equal(2, store.Writes);
        store.BeforeRead = null;
        Assert.Equal(Purple, runtime.CaptureConfiguration());
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public void AcknowledgedButMissingWrites_AreRejectedByIndependentObservation()
    {
        Store store = new() { IgnoreWrites = true };
        AccentColorRuntime runtime = Create(store);
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(Purple));
        Assert.Equal(System, runtime.CaptureConfiguration());
    }

    [Fact]
    public void InvalidResourceValue_CannotMasqueradeAsAbsenceOrAsASolidBrush()
    {
        Store store = new();
        AccentColorRuntime runtime = Create(store);
        store.Values["brush"] = null;
        Assert.Throws<InvalidOperationException>(() => runtime.CaptureConfiguration());
        runtime.Apply(Purple);
        store.Values["brush"] = null;
        Assert.Throws<InvalidOperationException>(() => runtime.CaptureConfiguration());
    }

    [Fact]
    public void FollowSystem_RemovesOnlyOwnedOverridesAndRetainsTheConfiguredCustomColor()
    {
        Store store = new();
        store.Values["unrelated"] = new(0x01234567, false);
        store.Merged["brush"] = new(0xFF112233, true);
        AccentColorRuntime runtime = Create(store);
        runtime.Apply(Purple);
        AccentColorConfiguration following = new(AppAccentColorMode.FollowSystem, Purple.ColorValue);
        runtime.Apply(following);
        Assert.Equal(following, runtime.CaptureConfiguration());
        Assert.Equal("unrelated", Assert.Single(store.Values).Key);
        Assert.Equal(new AccentResourceValue(0xFF112233, true), store.Merged["brush"]);
        Assert.Equal(["color", "brush"], store.RemovedKeys);
    }

    [Fact]
    public void IncompleteRemoval_CannotClaimTheSystemPaletteIsActive()
    {
        Store store = new();
        AccentColorRuntime runtime = Create(store);
        runtime.Apply(Purple);
        store.BeforeWrite = () => { if (store.Writes == 4) { throw new IOException("Isolated remove failure."); } };
        Assert.Throws<IOException>(() => runtime.Apply(System));
        Assert.Throws<InvalidOperationException>(() => runtime.CaptureConfiguration());
        store.BeforeWrite = null;
        runtime.Apply(System);
        Assert.Equal(System, runtime.CaptureConfiguration());
    }

    [Theory]
    [InlineData("preflight")]
    [InlineData("write")]
    [InlineData("verification")]
    public void FatalExceptionGraphs_KeepTheirIdentity(string stage)
    {
        Store store = new();
        AccentColorRuntime runtime = Create(store);
        InvalidOperationException fatal = new("Isolated wrapper.", new AggregateException(Activator.CreateInstance<OutOfMemoryException>()));
        if (stage == "write") { store.BeforeWrite = () => throw fatal; }
        else { store.BeforeRead = () => { if (store.Reads == (stage == "preflight" ? 1 : 2)) { throw fatal; } }; }
        Assert.Same(fatal, Assert.Throws<InvalidOperationException>(() => runtime.Apply(Purple)));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("foreign")]
    [InlineData("system-override")]
    public void InvalidPaletteContracts_AreRejectedBeforePlatformAccess(string kind)
    {
        Store store = new();
        bool invalid = false;
        AccentColorRuntime runtime = new(store, ["color", "brush"], configuration =>
        {
            if (!invalid) { return Build(configuration); }
            return kind == "foreign" ? new Dictionary<string, AccentResourceValue> { ["other"] = new(0, false) }
                : new Dictionary<string, AccentResourceValue> { ["color"] = new(0, false) };
        });
        invalid = true;
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(kind == "system-override" ? System : Purple));
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void PaletteInputs_AreCopiedBeforeEffectsCanChangeTheirSource()
    {
        Store store = new();
        string[] keys = ["color", "brush"];
        Dictionary<string, AccentResourceValue> source = Build(Purple);
        AccentColorRuntime runtime = new(store, keys, configuration => configuration.Mode == AppAccentColorMode.FollowSystem
            ? new Dictionary<string, AccentResourceValue>() : source);
        keys[0] = "unrelated";
        store.BeforeWrite = () => source.Clear();
        runtime.Apply(Purple);
        Assert.Equal(Purple, runtime.CaptureConfiguration());
        Assert.Equal(2, store.Values.Count);
        Assert.False(store.Values.ContainsKey("unrelated"));
    }

    private static AccentColorRuntime Create(Store store) => new(store, ["color", "brush"], Build);
    private static Dictionary<string, AccentResourceValue> Build(AccentColorConfiguration configuration)
    {
        if (configuration.Mode == AppAccentColorMode.FollowSystem) { return []; }
        uint argb = Convert.ToUInt32(configuration.ColorValue[1..], 16);
        return new(StringComparer.Ordinal) { ["color"] = new(argb, false), ["brush"] = new(argb, true) };
    }

    private sealed class Store : IAccentResourceStore
    {
        public Dictionary<string, AccentResourceValue?> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AccentResourceValue> Merged { get; } = new(StringComparer.Ordinal);
        public List<string> RemovedKeys { get; } = [];
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool IgnoreWrites { get; init; }
        public Action? BeforeRead { get; set; }
        public Action? BeforeWrite { get; set; }
        public Action? AfterWrite { get; set; }
        public IReadOnlyDictionary<string, AccentResourceValue?> CaptureLocalOverrides(IReadOnlyCollection<string> ownedKeys)
        {
            ++Reads;
            BeforeRead?.Invoke();
            return Values.Where(pair => ownedKeys.Contains(pair.Key, StringComparer.Ordinal)).ToDictionary();
        }
        public void WriteOverride(string key, AccentResourceValue value)
        {
            ++Writes;
            BeforeWrite?.Invoke();
            if (!IgnoreWrites) { Values[key] = value; }
            AfterWrite?.Invoke();
        }
        public void RemoveOverride(string key)
        {
            ++Writes;
            BeforeWrite?.Invoke();
            Values.Remove(key);
            RemovedKeys.Add(key);
            AfterWrite?.Invoke();
        }
    }
}
