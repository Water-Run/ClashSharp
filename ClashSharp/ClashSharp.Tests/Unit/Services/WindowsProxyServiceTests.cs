using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for full WinINet baseline and ownership semantics.</summary>
public sealed class WindowsProxyServiceTests
{
    [Fact]
    public void DisableProxy_AfterOwnedEnable_RestoresCompleteBaseline()
    {
        WindowsProxyRegistrySnapshot baseline = Snapshot(
            enabled: false,
            server: "corporate.example:8080",
            proxyOverride: "<local>;intranet.example",
            autoConfigUrl: "https://pac.example/proxy.pac");
        FakeWindowsProxyRegistryStore registry = new(baseline);
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);

        service.EnableProxy("127.0.0.1:19090");
        Assert.Equal("<local>", registry.Current.ProxyOverride.Value);
        Assert.False(registry.Current.AutoConfigUrl.Exists);
        service.DisableProxy();

        Assert.Equal(baseline, registry.Current);
        Assert.Null(journal.Current);
    }

    [Fact]
    public void DisableProxy_WhenTupleChangedExternally_DoesNotOverwriteExternalState()
    {
        FakeWindowsProxyRegistryStore registry = new(Snapshot(false, "old", "<local>", null));
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);
        service.EnableProxy("127.0.0.1:19090");
        WindowsProxyRegistrySnapshot external = registry.Current with
        {
            AutoConfigUrl = StringValue("https://external.example/pac"),
        };
        registry.Current = external;

        service.DisableProxy();

        Assert.Equal(external.AutoConfigUrl, registry.Current.AutoConfigUrl);
        Assert.Equal(0, registry.Current.ProxyEnable.Value);
        Assert.Equal("old", registry.Current.ProxyServer.Value);
        Assert.Equal("<local>", registry.Current.ProxyOverride.Value);
        Assert.Null(journal.Current);
    }

    [Fact]
    public void EnableProxy_WhenRepeated_PreservesOriginalBaseline()
    {
        WindowsProxyRegistrySnapshot baseline = Snapshot(true, "corporate:8080", "<local>", null);
        FakeWindowsProxyRegistryStore registry = new(baseline);
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);

        service.EnableProxy("127.0.0.1:10000");
        service.EnableProxy("127.0.0.1:20000");
        Assert.Equal(WindowsProxyMutationPhase.Applied, journal.Current?.Phase);
        Assert.Null(journal.Current?.PendingApplied);
        service.DisableProxy();

        Assert.Equal(baseline, registry.Current);
    }

    [Fact]
    public void EnableProxy_WhenRepeatedWriteFailsPartially_RestoresOriginalBaseline()
    {
        WindowsProxyRegistrySnapshot baseline = Snapshot(false, "corporate:8080", "corp", "https://corp/proxy.pac");
        FakeWindowsProxyRegistryStore registry = new(baseline);
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);
        service.EnableProxy("127.0.0.1:10000");
        registry.FailNextWritePartially = true;

        Assert.Throws<IOException>(() => service.EnableProxy("127.0.0.1:20000"));

        Assert.Equal(baseline, registry.Current);
        Assert.Null(journal.Current);
    }

    [Fact]
    public void DisableProxy_AfterCrashWithRepeatedApplyPending_RestoresOriginalBaseline()
    {
        WindowsProxyRegistrySnapshot baseline = Snapshot(false, "corporate:8080", "corp", null);
        FakeWindowsProxyRegistryStore registry = new(baseline);
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);
        service.EnableProxy("127.0.0.1:10000");
        journal.ThrowAfterNextWrite = true;

        Assert.Throws<IOException>(() => service.EnableProxy("127.0.0.1:20000"));
        Assert.Equal("127.0.0.1:10000", registry.Current.ProxyServer.Value);
        Assert.Equal(WindowsProxyMutationPhase.Applying, journal.Current?.Phase);
        Assert.Equal("127.0.0.1:10000", journal.Current?.Applied.ProxyServer.Value);
        Assert.Equal("127.0.0.1:20000", journal.Current?.PendingApplied?.ProxyServer.Value);
        WindowsProxyStringValue externalPac = StringValue("https://external.example/proxy.pac");
        registry.Current = registry.Current with { AutoConfigUrl = externalPac };

        new WindowsProxyService(registry, journal).DisableProxy();

        Assert.Equal(baseline.ProxyEnable, registry.Current.ProxyEnable);
        Assert.Equal(baseline.ProxyServer, registry.Current.ProxyServer);
        Assert.Equal(baseline.ProxyOverride, registry.Current.ProxyOverride);
        Assert.Equal(externalPac, registry.Current.AutoConfigUrl);
        Assert.Null(journal.Current);
    }

    [Fact]
    public void EnableProxy_WhenExternalPacChanges_RollsBaselineForwardPerField()
    {
        WindowsProxyRegistrySnapshot baseline = Snapshot(false, "corporate:8080", "corp", "https://old/pac");
        FakeWindowsProxyRegistryStore registry = new(baseline);
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);
        service.EnableProxy("127.0.0.1:10000");
        WindowsProxyStringValue externalPac = StringValue("https://external/new.pac");
        registry.Current = registry.Current with { AutoConfigUrl = externalPac };

        service.EnableProxy("127.0.0.1:20000");
        service.DisableProxy();

        Assert.Equal(0, registry.Current.ProxyEnable.Value);
        Assert.Equal("corporate:8080", registry.Current.ProxyServer.Value);
        Assert.Equal("corp", registry.Current.ProxyOverride.Value);
        Assert.Equal(externalPac, registry.Current.AutoConfigUrl);
    }

    [Fact]
    public void DisableProxy_WithoutOwnershipJournal_DoesNotMutateWinInet()
    {
        WindowsProxyRegistrySnapshot external = Snapshot(true, "external:3128", "<local>", null);
        FakeWindowsProxyRegistryStore registry = new(external);
        WindowsProxyService service = new(registry, new FakeWindowsProxyMutationJournalStore());

        service.DisableProxy();

        Assert.Equal(external, registry.Current);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public void DisableProxy_WithoutJournalButExactLegacyEndpoint_DisablesOnlyLegacyEndpoint()
    {
        WindowsProxyRegistrySnapshot legacy = Snapshot(true, "127.0.0.1:19090", "<local>", null);
        FakeWindowsProxyRegistryStore registry = new(legacy);
        WindowsProxyService service = new(
            registry,
            new FakeWindowsProxyMutationJournalStore(),
            getManagedPort: () => 19090);

        service.DisableProxy();

        Assert.Equal(0, registry.Current.ProxyEnable.Value);
        Assert.Equal(legacy.ProxyServer, registry.Current.ProxyServer);
    }

    [Fact]
    public void DisableProxy_AfterPartialApplyFailure_FailsClosedWithoutOverwritingTuple()
    {
        WindowsProxyRegistrySnapshot baseline = Snapshot(false, "old", "<local>", null);
        FakeWindowsProxyRegistryStore registry = new(baseline) { FailNextWritePartially = true };
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);

        Assert.Throws<IOException>(() => service.EnableProxy("127.0.0.1:19090"));

        Assert.Equal(baseline, registry.Current);
        Assert.Null(journal.Current);
    }

    [Fact]
    public void DisableProxy_RestoresRegistryPresenceAndStringKindsExactly()
    {
        WindowsProxyRegistrySnapshot baseline = new(
            new WindowsProxyDwordValue(false, 0),
            new WindowsProxyStringValue(false, null, WindowsProxyStringKind.None),
            new WindowsProxyStringValue(true, "%USERPROFILE%\\intranet", WindowsProxyStringKind.ExpandString),
            new WindowsProxyStringValue(true, "%CORP_PAC%", WindowsProxyStringKind.ExpandString));
        FakeWindowsProxyRegistryStore registry = new(baseline);
        FakeWindowsProxyMutationJournalStore journal = new();
        WindowsProxyService service = new(registry, journal);

        service.EnableProxy("127.0.0.1:19090");
        service.DisableProxy();

        Assert.Equal(baseline, registry.Current);
    }

    private static WindowsProxyRegistrySnapshot Snapshot(
        bool enabled,
        string? server,
        string? proxyOverride,
        string? autoConfigUrl)
    {
        return new WindowsProxyRegistrySnapshot(
            new WindowsProxyDwordValue(true, enabled ? 1 : 0),
            StringValue(server),
            StringValue(proxyOverride),
            StringValue(autoConfigUrl));
    }

    private static WindowsProxyStringValue StringValue(string? value)
    {
        return value is null
            ? new WindowsProxyStringValue(false, null, WindowsProxyStringKind.None)
            : new WindowsProxyStringValue(true, value, WindowsProxyStringKind.String);
    }

    private sealed class FakeWindowsProxyRegistryStore(WindowsProxyRegistrySnapshot initial) : IWindowsProxyRegistryStore
    {
        public WindowsProxyRegistrySnapshot Current { get; set; } = initial;

        public bool FailNextWritePartially { get; set; }

        public int WriteCount { get; private set; }

        public WindowsProxyRegistrySnapshot Read()
        {
            return Current;
        }

        public void Write(WindowsProxyRegistrySnapshot snapshot)
        {
            WriteCount++;
            if (FailNextWritePartially)
            {
                FailNextWritePartially = false;
                Current = Current with
                {
                    ProxyEnable = snapshot.ProxyEnable,
                    ProxyServer = snapshot.ProxyServer,
                };
                throw new IOException("simulated registry write failure");
            }

            Current = snapshot;
        }
    }

    private sealed class FakeWindowsProxyMutationJournalStore : IWindowsProxyMutationJournalStore
    {
        public WindowsProxyMutationJournal? Current { get; private set; }

        public bool ThrowAfterNextWrite { get; set; }

        public WindowsProxyMutationJournal? Read()
        {
            return Current;
        }

        public void Write(WindowsProxyMutationJournal journal)
        {
            Current = journal;
            if (ThrowAfterNextWrite)
            {
                ThrowAfterNextWrite = false;
                throw new IOException("simulated process crash after durable journal write");
            }
        }

        public void Clear()
        {
            Current = null;
        }
    }
}
