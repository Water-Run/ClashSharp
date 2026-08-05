using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for Windows network diagnostics.</summary>
public sealed class WindowsNetworkDiagnosticServiceTests
{
    /// <summary>Verifies terminal diagnosis reads injected environment variables and localized text.</summary>
    [Fact]
    public async Task DiagnoseAsync_WhenTerminalProxyEnvironmentMatchesConfiguredPort_ReturnsReady()
    {
        FakeWindowsDiagnosticEnvironment environment = new()
        {
            Variables =
            {
                ["HTTP_PROXY"] = "http://127.0.0.1:19090",
                ["HTTPS_PROXY"] = "http://127.0.0.1:19090",
                ["ALL_PROXY"] = "http://127.0.0.1:19090",
                ["NO_PROXY"] = "localhost,127.0.0.1,::1",
            },
        };
        WindowsNetworkDiagnosticService service = CreateService(environment);

        WindowsDiagnosticResult result = await service.DiagnoseAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.Equal(WindowsDiagnosticTarget.Terminal, result.Target);
        Assert.Equal("terminal target", result.DisplayName);
        Assert.True(result.IsHealthy);
        Assert.Equal("terminal ready", result.Message);
        Assert.Contains("HTTP_PROXY=http://127.0.0.1:19090", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>Verifies applying terminal repair writes proxy environment variables through the injected environment.</summary>
    [Fact]
    public async Task ApplyAsync_WhenTerminalTarget_SetsProxyEnvironmentVariablesAndReturnsReady()
    {
        FakeWindowsDiagnosticEnvironment environment = new();
        WindowsNetworkDiagnosticService service = CreateService(environment);

        WindowsDiagnosticResult result = await service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.True(result.IsHealthy);
        Assert.Equal("terminal ready", result.Message);
        Assert.Equal("http://127.0.0.1:19090", environment.Variables["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:19090", environment.Variables["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:19090", environment.Variables["ALL_PROXY"]);
        Assert.Equal("localhost,127.0.0.1,::1", environment.Variables["NO_PROXY"]);
    }

    /// <summary>Verifies a completed apply finalizes every pending environment mutation.</summary>
    [Fact]
    public async Task ApplyAsync_WhenEnvironmentWritesComplete_FinalizesJournal()
    {
        FakeWindowsDiagnosticEnvironment environment = new();
        FakeWindowsDiagnosticMutationJournalStore journal = new();

        await CreateService(environment, journal).ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.All(journal.State.EnvironmentVariables.Values, mutation =>
        {
            Assert.Equal(WindowsDiagnosticMutationPhase.Applied, mutation.Phase);
            Assert.Null(mutation.PendingAppliedValue);
            Assert.NotNull(mutation.AppliedValue);
        });
    }

    /// <summary>Verifies reset restores the exact durable environment baseline rather than clearing user values.</summary>
    [Fact]
    public async Task ResetAsync_AfterTerminalApply_RestoresExactBaselineAcrossServiceInstances()
    {
        FakeWindowsDiagnosticEnvironment environment = new()
        {
            Variables =
            {
                ["HTTP_PROXY"] = "http://corporate.example:8080",
                ["ALL_PROXY"] = "socks5://corporate.example:1080",
                ["NO_PROXY"] = "intranet.example",
            },
        };
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        await CreateService(environment, journal).ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        await CreateService(environment, journal).ResetAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.Equal("http://corporate.example:8080", environment.Variables["HTTP_PROXY"]);
        Assert.False(environment.Variables.ContainsKey("HTTPS_PROXY"));
        Assert.Equal("socks5://corporate.example:1080", environment.Variables["ALL_PROXY"]);
        Assert.Equal("intranet.example", environment.Variables["NO_PROXY"]);
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies reset does not overwrite a value changed by another owner after Clash# apply.</summary>
    [Fact]
    public async Task ResetAsync_WhenEnvironmentChangedExternally_PreservesExternalValue()
    {
        FakeWindowsDiagnosticEnvironment environment = new();
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        WindowsNetworkDiagnosticService service = CreateService(environment, journal);
        await service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);
        environment.Variables["HTTP_PROXY"] = "http://external.example:3128";

        await service.ResetAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.Equal("http://external.example:3128", environment.Variables["HTTP_PROXY"]);
        Assert.False(environment.Variables.ContainsKey("HTTPS_PROXY"));
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies a partially failed apply restores only fields that still equal the planned Clash# values.</summary>
    [Fact]
    public async Task ResetAsync_AfterPartialEnvironmentApplyFailure_RestoresOnlyOwnedAppliedFields()
    {
        FakeWindowsDiagnosticEnvironment environment = new()
        {
            Variables = { ["HTTP_PROXY"] = "http://baseline.example:8080" },
            FailOnSetCall = 2,
        };
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        WindowsNetworkDiagnosticService service = CreateService(environment, journal);
        Assert.Throws<IOException>(() =>
            service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None).GetAwaiter().GetResult());
        environment.FailOnSetCall = null;

        await service.ResetAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.Equal("http://baseline.example:8080", environment.Variables["HTTP_PROXY"]);
        Assert.False(environment.Variables.ContainsKey("HTTPS_PROXY"));
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies a failed second apply recognizes both old-port and new-port fields as owned.</summary>
    [Fact]
    public async Task ResetAsync_AfterRepeatedApplyPartiallyWritesNewPort_RestoresOriginalBaseline()
    {
        FakeWindowsDiagnosticEnvironment environment = new()
        {
            Variables =
            {
                ["HTTP_PROXY"] = "http://corporate.example:8080",
                ["ALL_PROXY"] = "socks5://corporate.example:1080",
                ["NO_PROXY"] = "intranet.example",
            },
        };
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        FakeWindowsDiagnosticSettings settings = new() { MixedPort = 19090 };
        WindowsNetworkDiagnosticService service = CreateService(environment, journal, settings: settings);
        await service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);
        settings.MixedPort = 20000;
        environment.FailOnSetCall = environment.SetCallCount + 2;

        await Assert.ThrowsAsync<IOException>(() =>
            service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None));
        environment.FailOnSetCall = null;
        await CreateService(environment, journal, settings: settings)
            .ResetAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.Equal("http://corporate.example:8080", environment.Variables["HTTP_PROXY"]);
        Assert.False(environment.Variables.ContainsKey("HTTPS_PROXY"));
        Assert.Equal("socks5://corporate.example:1080", environment.Variables["ALL_PROXY"]);
        Assert.Equal("intranet.example", environment.Variables["NO_PROXY"]);
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies recovery after a crash immediately after the second pending journal write.</summary>
    [Fact]
    public async Task ResetAsync_AfterCrashWithRepeatedApplyPending_RestoresOriginalBaseline()
    {
        FakeWindowsDiagnosticEnvironment environment = new()
        {
            Variables = { ["HTTP_PROXY"] = "http://corporate.example:8080" },
        };
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        FakeWindowsDiagnosticSettings settings = new() { MixedPort = 19090 };
        WindowsNetworkDiagnosticService service = CreateService(environment, journal, settings: settings);
        await service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);
        settings.MixedPort = 20000;
        journal.ThrowAfterNextWrite = true;

        await Assert.ThrowsAsync<IOException>(() =>
            service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None));
        Assert.All(journal.State.EnvironmentVariables.Values, mutation =>
        {
            Assert.Equal(WindowsDiagnosticMutationPhase.Applying, mutation.Phase);
        });
        Assert.Equal("http://127.0.0.1:20000", journal.State.EnvironmentVariables["HTTP_PROXY"].PendingAppliedValue);
        Assert.Equal("http://127.0.0.1:20000", journal.State.EnvironmentVariables["HTTPS_PROXY"].PendingAppliedValue);
        Assert.Equal("http://127.0.0.1:20000", journal.State.EnvironmentVariables["ALL_PROXY"].PendingAppliedValue);
        Assert.Equal("localhost,127.0.0.1,::1", journal.State.EnvironmentVariables["NO_PROXY"].PendingAppliedValue);
        environment.Variables["ALL_PROXY"] = "socks5://external.example:1080";

        await CreateService(environment, journal, settings: settings)
            .ResetAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.Equal("http://corporate.example:8080", environment.Variables["HTTP_PROXY"]);
        Assert.False(environment.Variables.ContainsKey("HTTPS_PROXY"));
        Assert.Equal("socks5://external.example:1080", environment.Variables["ALL_PROXY"]);
        Assert.False(environment.Variables.ContainsKey("NO_PROXY"));
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies shared environment values remain applied until their final diagnostic owner resets them.</summary>
    [Fact]
    public async Task ResetAsync_WithWslAndTerminalOwners_RestoresOnlyAfterFinalOwnerReleases()
    {
        FakeWindowsDiagnosticEnvironment environment = new()
        {
            Variables = { ["WSLENV"] = "USERPROFILE/up" },
        };
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        WindowsNetworkDiagnosticService service = CreateService(environment, journal);
        await service.ApplyAsync(WindowsDiagnosticTarget.Wsl, CancellationToken.None);
        await service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        await service.ResetAsync(WindowsDiagnosticTarget.Wsl, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:19090", environment.Variables["HTTP_PROXY"]);
        Assert.Equal("USERPROFILE/up", environment.Variables["WSLENV"]);

        await service.ResetAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.False(environment.Variables.ContainsKey("HTTP_PROXY"));
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies a preexisting Store exemption is never removed by reset.</summary>
    [Fact]
    public async Task ResetAsync_WhenStoreExemptionPreexists_PreservesExemption()
    {
        FakeWindowsDiagnosticProcessRunner processRunner = new() { StoreExemptionPresent = true };
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        WindowsNetworkDiagnosticService service = CreateService(new FakeWindowsDiagnosticEnvironment(), journal, processRunner);

        await service.ApplyAsync(WindowsDiagnosticTarget.MicrosoftStore, CancellationToken.None);
        await service.ResetAsync(WindowsDiagnosticTarget.MicrosoftStore, CancellationToken.None);

        Assert.True(processRunner.StoreExemptionPresent);
        Assert.Equal(0, processRunner.AddCalls);
        Assert.Equal(0, processRunner.DeleteCalls);
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies an exemption created by Clash# is removed when it remains Clash#-owned.</summary>
    [Fact]
    public async Task ResetAsync_WhenStoreExemptionWasAdded_RestoresAbsentBaseline()
    {
        FakeWindowsDiagnosticProcessRunner processRunner = new();
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        WindowsNetworkDiagnosticService service = CreateService(new FakeWindowsDiagnosticEnvironment(), journal, processRunner);

        await service.ApplyAsync(WindowsDiagnosticTarget.MicrosoftStore, CancellationToken.None);
        await service.ResetAsync(WindowsDiagnosticTarget.MicrosoftStore, CancellationToken.None);

        Assert.False(processRunner.StoreExemptionPresent);
        Assert.Equal(1, processRunner.AddCalls);
        Assert.Equal(1, processRunner.DeleteCalls);
        Assert.True(journal.State.IsEmpty);
    }

    /// <summary>Verifies a failed Store apply leaves a pending journal that reset can safely release.</summary>
    [Fact]
    public async Task ResetAsync_AfterStoreApplyFails_RecoversPendingJournal()
    {
        FakeWindowsDiagnosticProcessRunner processRunner = new() { FailNextAdd = true };
        FakeWindowsDiagnosticMutationJournalStore journal = new();
        WindowsNetworkDiagnosticService service = CreateService(new FakeWindowsDiagnosticEnvironment(), journal, processRunner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(WindowsDiagnosticTarget.MicrosoftStore, CancellationToken.None));
        Assert.Equal(WindowsDiagnosticMutationPhase.Applying, journal.State.MicrosoftStore?.Phase);
        Assert.True(journal.State.MicrosoftStore?.PendingAppliedPresent is true);

        await service.ResetAsync(WindowsDiagnosticTarget.MicrosoftStore, CancellationToken.None);

        Assert.False(processRunner.StoreExemptionPresent);
        Assert.True(journal.State.IsEmpty);
    }

    private static WindowsNetworkDiagnosticService CreateService(
        FakeWindowsDiagnosticEnvironment environment,
        FakeWindowsDiagnosticMutationJournalStore? journal = null,
        FakeWindowsDiagnosticProcessRunner? processRunner = null,
        FakeWindowsDiagnosticSettings? settings = null)
    {
        return new WindowsNetworkDiagnosticService(
            settings ?? new FakeWindowsDiagnosticSettings { MixedPort = 19090 },
            environment,
            processRunner ?? new FakeWindowsDiagnosticProcessRunner(),
            journal ?? new FakeWindowsDiagnosticMutationJournalStore(),
            key => key switch
            {
                "WindowsDiagnostic.Target.Terminal" => "terminal target",
                "WindowsDiagnostic.Terminal.Ready" => "terminal ready",
                "WindowsDiagnostic.Terminal.ProxyEnvironmentMissing" => "terminal missing",
                _ => key,
            });
    }

    private sealed class FakeWindowsDiagnosticMutationJournalStore : IWindowsDiagnosticMutationJournalStore
    {
        public WindowsDiagnosticMutationJournal State { get; private set; } = WindowsDiagnosticMutationJournal.Empty();

        public bool ThrowAfterNextWrite { get; set; }

        public WindowsDiagnosticMutationJournal Read()
        {
            return Clone(State);
        }

        public void Write(WindowsDiagnosticMutationJournal journal)
        {
            State = Clone(journal);
            if (ThrowAfterNextWrite)
            {
                ThrowAfterNextWrite = false;
                throw new IOException("simulated process crash after durable journal write");
            }
        }

        private static WindowsDiagnosticMutationJournal Clone(WindowsDiagnosticMutationJournal journal)
        {
            return journal with
            {
                EnvironmentVariables = new Dictionary<string, WindowsDiagnosticEnvironmentMutation>(
                    journal.EnvironmentVariables,
                    StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    private sealed class FakeWindowsDiagnosticSettings : IWindowsDiagnosticSettings
    {
        public int MixedPort { get; set; }
    }

    private sealed class FakeWindowsDiagnosticEnvironment : IWindowsDiagnosticEnvironment
    {
        private int _setCallCount;

        public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int? FailOnSetCall { get; set; }

        public int SetCallCount => _setCallCount;

        public string? GetUserEnvironmentVariable(string name)
        {
            return Variables.GetValueOrDefault(name);
        }

        public void SetUserEnvironmentVariable(string name, string? value)
        {
            _setCallCount++;
            if (_setCallCount == FailOnSetCall)
            {
                throw new IOException("simulated environment write failure");
            }

            if (value is null)
            {
                Variables.Remove(name);
                return;
            }

            Variables[name] = value;
        }
    }

    private sealed class FakeWindowsDiagnosticProcessRunner : IWindowsDiagnosticProcessRunner
    {
        public bool StoreExemptionPresent { get; set; }

        public int AddCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public bool FailNextAdd { get; set; }

        public Task<WindowsDiagnosticProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(fileName, "CheckNetIsolation.exe"))
            {
                if (arguments.Contains("-a", StringComparer.Ordinal))
                {
                    AddCalls++;
                    if (FailNextAdd)
                    {
                        FailNextAdd = false;
                        return Task.FromResult(new WindowsDiagnosticProcessResult(1, string.Empty, "simulated add failure"));
                    }

                    StoreExemptionPresent = true;
                }
                else if (arguments.Contains("-d", StringComparer.Ordinal))
                {
                    DeleteCalls++;
                    StoreExemptionPresent = false;
                }

                string output = StoreExemptionPresent
                    ? "Microsoft.WindowsStore_8wekyb3d8bbwe"
                    : string.Empty;
                return Task.FromResult(new WindowsDiagnosticProcessResult(0, output, string.Empty));
            }

            return Task.FromResult(new WindowsDiagnosticProcessResult(0, string.Empty, string.Empty));
        }
    }
}
