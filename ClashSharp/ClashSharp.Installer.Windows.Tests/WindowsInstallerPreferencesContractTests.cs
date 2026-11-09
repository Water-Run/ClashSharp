using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Universe and property tests of persisted user intent: the codec accepts exactly its own canonical
/// output at every truncation and substitution, and the coordinator keeps the intent state machine
/// consistent for every pending, installed, selected and durable combination.
/// </summary>
public sealed class WindowsInstallerPreferencesContractTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private static readonly char[] Substitutions = ['\0', ' ', '\n', '"', ',', ':', '0', '9', 'a', 'f', 'z', '{', '}', '[', ']', '\\', '-', 'é', '\uFEFF'];

    [Fact]
    public void EveryRepresentableIntentRoundTripsToTheSameCanonicalText()
    {
        using var fixture = Fixture();
        string family = fixture.Manifest.PackageIdentity.PackageFamilyName;
        int count = 0;
        foreach (InstallerNotificationPreset notification in Enum.GetValues<InstallerNotificationPreset>())
        {
            for (int bits = 0; bits < 64; bits++)
            {
                foreach (bool initialize in new[] { true, false })
                {
                    foreach (bool verified in new[] { true, false })
                    {
                        var preferences = new InstallerPreferences
                        {
                            CreateDesktopShortcut = (bits & 1) != 0,
                            RequestTaskbarPin = (bits & 2) != 0,
                            LaunchAtStartup = (bits & 4) != 0,
                            RegisterProxyRecovery = (bits & 8) != 0,
                            InstallTransparentProxyService = (bits & 16) != 0,
                            FollowSystemDefaults = (bits & 32) != 0,
                            Notifications = notification,
                            EnableMcpServer = initialize != verified,
                        };
                        WindowsInstallerPreferencesDocument document = Document(fixture, preferences) with
                        {
                            InitializeApplicationDefaults = initialize,
                            CoreVerified = verified,
                        };

                        string encoded = WindowsInstallerPreferencesStore.Encode(document, family);
                        WindowsInstallerPreferencesDocument decoded = WindowsInstallerPreferencesStore.Decode(encoded, family);

                        Assert.Equal(document, decoded);
                        Assert.Equal(encoded, WindowsInstallerPreferencesStore.Encode(decoded, family));
                        Assert.True(Encoding.UTF8.GetByteCount(encoded) < 1024);
                        count++;
                    }
                }
            }
        }

        Assert.Equal(4 * 64 * 4, count);
    }

    [Fact]
    public void EveryTruncationAndSubstitutionIsRejectedOrIsTheCanonicalFormOfWhatItDecodesTo()
    {
        using var fixture = Fixture();
        string family = fixture.Manifest.PackageIdentity.PackageFamilyName;
        string encoded = WindowsInstallerPreferencesStore.Encode(
            Document(fixture, InstallerPreferences.Default with { EnableMcpServer = true, Notifications = InstallerNotificationPreset.CriticalOnly }),
            family);

        for (int length = 0; length < encoded.Length; length++)
        {
            string truncated = encoded[..length];
            Exception? failure = Record.Exception(() => WindowsInstallerPreferencesStore.Decode(truncated, family));
            Assert.True(
                failure is InstallerProtocolException,
                $"Truncation to {length} produced {failure?.GetType().Name ?? "success"}.");
        }

        int accepted = 0;
        int rejected = 0;
        for (int offset = 0; offset < encoded.Length; offset++)
        {
            foreach (char replacement in Substitutions)
            {
                if (encoded[offset] == replacement)
                {
                    continue;
                }

                string mutated = string.Concat(encoded.AsSpan(0, offset), replacement.ToString(), encoded.AsSpan(offset + 1));
                try
                {
                    WindowsInstallerPreferencesDocument decoded = WindowsInstallerPreferencesStore.Decode(mutated, family);
                    Assert.Equal(mutated, WindowsInstallerPreferencesStore.Encode(decoded, family));
                    accepted++;
                }
                catch (InstallerProtocolException)
                {
                    rejected++;
                }
            }
        }

        Assert.True(rejected > accepted);
    }

    [Fact]
    public void EquivalentJsonInAnyOtherEnvelopeIsRejectedAsNoncanonical()
    {
        using var fixture = Fixture();
        string family = fixture.Manifest.PackageIdentity.PackageFamilyName;
        string canonical = WindowsInstallerPreferencesStore.Encode(Document(fixture, InstallerPreferences.Default), family);
        string[] variants =
        [
            "\uFEFF" + canonical,
            canonical + "\n",
            canonical + "\r\n",
            " " + canonical,
            canonical.Replace(",", ",\n", StringComparison.Ordinal),
            canonical.Replace(":", ": ", StringComparison.Ordinal),
            canonical.Replace("\"schema\"", "\"\\u0073chema\"", StringComparison.Ordinal),
            canonical.Replace("\"schema\":1", "\"schema\":1.0", StringComparison.Ordinal),
            canonical.Replace("\"schema\":1", "\"schema\":01", StringComparison.Ordinal),
            canonical.Replace("\"schema\"", "\"Schema\"", StringComparison.Ordinal),
            canonical.Replace("true", "\"true\"", StringComparison.Ordinal),
            canonical.Replace("\"preferences\":{", "\"preferences\":{\"createDesktopShortcut\":true,", StringComparison.Ordinal),
            canonical[..^1] + ",\"extra\":1}",
            "{\"preferences\":null}",
            "null",
            "[]",
            "42",
            string.Empty,
        ];

        foreach (string variant in variants)
        {
            Exception? failure = Record.Exception(() => WindowsInstallerPreferencesStore.Decode(variant, family));

            Assert.True(failure is InstallerProtocolException, $"'{variant}' produced {failure?.GetType().Name ?? "success"}.");
        }
    }

    [Fact]
    public void DocumentBudgetIsMeasuredInUtf8BytesAndCheckedBeforeParsing()
    {
        using var fixture = Fixture();
        string family = fixture.Manifest.PackageIdentity.PackageFamilyName;
        string canonical = WindowsInstallerPreferencesStore.Encode(Document(fixture, InstallerPreferences.Default), family);

        string atLimit = canonical + new string(' ', 8192 - canonical.Length);
        string overLimit = atLimit + " ";
        string multibyte = canonical + new string('é', 4096);

        Assert.Equal(
            "installer.preferences.document_noncanonical",
            Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPreferencesStore.Decode(atLimit, family)).DiagnosticCode);
        Assert.Equal(
            "installer.preferences.document_budget",
            Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPreferencesStore.Decode(overLimit, family)).DiagnosticCode);
        Assert.Equal(
            "installer.preferences.document_budget",
            Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPreferencesStore.Decode(multibyte, family)).DiagnosticCode);
        Assert.True(multibyte.Length < 8192);
    }

    [Fact]
    public void EveryInvalidFieldIsRefusedByEncodingWithAStableProtocolFailure()
    {
        using var fixture = Fixture();
        string family = fixture.Manifest.PackageIdentity.PackageFamilyName;
        WindowsInstallerPreferencesDocument valid = Document(fixture, InstallerPreferences.Default);
        string good = valid.Id;
        var invalid = new List<(string Label, WindowsInstallerPreferencesDocument Document)>();
        foreach (int schema in new[] { 0, 2, -1, int.MaxValue })
        {
            invalid.Add(("schema " + schema, valid with { Schema = schema }));
        }

        foreach (string id in new[]
        {
            good.ToUpperInvariant(), Guid.Parse(good).ToString("D"), "{" + good + "}", new string('0', 32), good[..31], good + "0",
            new string('g', 32), string.Empty, " " + good[1..],
        })
        {
            invalid.Add(("id " + id, valid with { Id = id }));
        }

        foreach (string version in new[] { "1.2.3", "1.2.3.4.5", "01.2.3.4", "1.2.3.-4", string.Empty, "a.b.c.d", "1.2.3.4 ", "99999999999.1.1.1" })
        {
            invalid.Add(("version " + version, valid with { PackageVersion = version }));
        }

        foreach (string hash in new[] { valid.PayloadSha256.ToUpperInvariant(), valid.PayloadSha256[..63], valid.PayloadSha256 + "0", new string('g', 64), string.Empty })
        {
            invalid.Add(("payload " + hash, valid with { PayloadSha256 = hash }));
        }

        foreach (string otherFamily in new[] { family.ToUpperInvariant(), family + "x", string.Empty, "Other_" + family })
        {
            invalid.Add(("family " + otherFamily, valid with { PackageFamilyName = otherFamily }));
        }

        foreach (int notification in new[] { -1, 4, 99, int.MaxValue })
        {
            invalid.Add(("notification " + notification, valid with
            {
                Preferences = InstallerPreferences.Default with { Notifications = (InstallerNotificationPreset)notification },
            }));
        }

        invalid.Add(("null preferences", valid with { Preferences = null! }));
        Assert.True(invalid.Count > 30);
        foreach ((string label, WindowsInstallerPreferencesDocument document) in invalid)
        {
            Exception? failure = Record.Exception(() => WindowsInstallerPreferencesStore.Encode(document, family));

            Assert.True(failure is InstallerProtocolException, $"{label} was encoded: {failure?.GetType().Name ?? "success"}.");
        }
    }

    [Fact]
    public void PreparationNeverMutatesBeyondOneStageAndKeepsTheIntentStateMachineConsistent()
    {
        using var fixture = Fixture();
        InstallerPreferences resumed = InstallerPreferences.Default with { EnableMcpServer = true };
        InstallerPreferences installedPreferences = InstallerPreferences.Default with { InstallTransparentProxyService = false };
        InstallerPreferences differentMcp = resumed with { EnableMcpServer = false };
        InstallerPreferences flippedService = resumed with { InstallTransparentProxyService = false };
        int combinations = 0;
        int refused = 0;
        foreach (string pendingKind in new[] { "none", "unverified", "verified", "otherVersion", "otherPayload" })
        {
            foreach (bool hasInstalled in new[] { false, true })
            {
                foreach ((string selectedLabel, InstallerPreferences? selected) in new (string, InstallerPreferences?)[]
                {
                    ("none", null), ("same", resumed), ("differentMcp", differentMcp), ("flippedService", flippedService),
                })
                {
                    foreach (bool? durableService in new bool?[] { null, true, false })
                    {
                        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
                        {
                            var store = new EnginePreferencesStore();
                            store.Pending = pendingKind switch
                            {
                                "none" => null,
                                "unverified" => Document(fixture, resumed),
                                "verified" => Document(fixture, resumed) with { CoreVerified = true },
                                "otherVersion" => Document(fixture, resumed) with { PackageVersion = "9.9.9.9" },
                                _ => Document(fixture, resumed) with { PayloadSha256 = new string('c', 64) },
                            };
                            store.Installed = hasInstalled ? Document(fixture, installedPreferences) with { CoreVerified = true } : null;
                            WindowsInstallerPreferencesDocument? pendingBefore = store.Pending;
                            WindowsInstallerPreferencesDocument? installedBefore = store.Installed;
                            InstallerTransactionJournal? journal = durableService is { } service
                                ? InstallerTransactionJournal.Create(fixture.Request(InstallerOperation.Install, Sid) with
                                {
                                    InstallTransparentProxyService = service,
                                })
                                : null;
                            var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new EnginePreferencesShell(), setup: new EnginePreferencesSetup());
                            combinations++;

                            WindowsInstallerPreferencesPlan? plan = null;
                            Exception? failure = Record.Exception(() => plan = coordinator.Prepare(operation, selected, journal));

                            string label = $"{operation}/{pendingKind}/installed={hasInstalled}/selected={selectedLabel}/durable={durableService}";
                            if (failure is not null)
                            {
                                refused++;
                                Assert.True(
                                    failure is InstallerProtocolException
                                    {
                                        DiagnosticCode: "installer.preferences.service_profile_conflict" or "installer.preferences.pending_intent_conflict",
                                    },
                                    $"{label}: {failure.GetType().Name} {(failure as InstallerProtocolException)?.DiagnosticCode}");
                                Assert.Equal(0, store.Stages);
                                Assert.Same(pendingBefore, store.Pending);
                                Assert.Same(installedBefore, store.Installed);
                                continue;
                            }

                            Assert.NotNull(plan);
                            bool matchesRelease = pendingBefore is not null
                                && pendingBefore.PackageVersion == fixture.Manifest.ExpectedPackageVersion
                                && pendingBefore.PayloadSha256 == fixture.Manifest.InstallerPayloadSha256;
                            if (operation == InstallerOperation.Uninstall)
                            {
                                Assert.Equal(0, store.Stages);
                                Assert.Null(plan.Document);
                                Assert.Same(pendingBefore, plan.Pending);
                                Assert.Same(installedBefore, plan.Installed);
                                Assert.Equal(
                                    journal?.InstallTransparentProxyService
                                        ?? pendingBefore?.Preferences.InstallTransparentProxyService
                                        ?? installedBefore?.Preferences.InstallTransparentProxyService
                                        ?? true,
                                    plan.IncludeService);
                                continue;
                            }

                            InstallerPreferences? resolved = selected ?? pendingBefore?.Preferences ?? installedBefore?.Preferences;
                            if (plan.Document is null)
                            {
                                Assert.Null(resolved);
                                Assert.Equal(0, store.Stages);
                                Assert.Equal(journal?.InstallTransparentProxyService ?? true, plan.IncludeService);
                                continue;
                            }

                            WindowsInstallerPreferencesDocument document = plan.Document;
                            Assert.Equal(1, store.Stages);
                            Assert.Same(document, store.Pending);
                            Assert.False(document.CoreVerified);
                            Assert.Equal(fixture.Manifest.PackageIdentity.PackageFamilyName, document.PackageFamilyName);
                            Assert.Equal(fixture.Manifest.ExpectedPackageVersion, document.PackageVersion);
                            Assert.Equal(fixture.Manifest.InstallerPayloadSha256, document.PayloadSha256);
                            Assert.Equal(resolved, document.Preferences);
                            Assert.Equal(document.Preferences.InstallTransparentProxyService, plan.IncludeService);
                            Assert.Equal(journal?.InstallTransparentProxyService ?? resolved!.InstallTransparentProxyService, plan.IncludeService);
                            bool resumes = matchesRelease && resolved == pendingBefore!.Preferences;
                            Assert.Equal(resumes, pendingBefore is not null && document.Id == pendingBefore.Id);
                            Assert.True(Guid.TryParseExact(document.Id, "N", out _));

                            // Re-preparing the staged intent must resume it, never fork a second identity.
                            if (journal is null)
                            {
                                WindowsInstallerPreferencesPlan again = coordinator.Prepare(operation, null, null);
                                Assert.Equal(document.Id, again.Document!.Id);
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(5 * 2 * 4 * 3 * 3, combinations);
        Assert.InRange(refused, 1, combinations - 1);
    }

    [Fact]
    public void PendingAndServiceProfileFollowTheirTruthTables()
    {
        using var fixture = Fixture();
        foreach (string pendingKind in new[] { "none", "unverified", "verified", "otherVersion", "otherPayload" })
        {
            foreach (bool? installedService in new bool?[] { null, true, false })
            {
                foreach (bool pendingService in new[] { true, false })
                {
                    InstallerPreferences prefs = InstallerPreferences.Default with { InstallTransparentProxyService = pendingService };
                    WindowsInstallerPreferencesDocument? pending = pendingKind switch
                    {
                        "none" => null,
                        "unverified" => Document(fixture, prefs),
                        "verified" => Document(fixture, prefs) with { CoreVerified = true },
                        "otherVersion" => Document(fixture, prefs) with { PackageVersion = "9.9.9.9", CoreVerified = true },
                        _ => Document(fixture, prefs) with { PayloadSha256 = new string('d', 64), CoreVerified = true },
                    };
                    var store = new EnginePreferencesStore
                    {
                        Pending = pending,
                        Installed = installedService is { } flag
                            ? Document(fixture, InstallerPreferences.Default with { InstallTransparentProxyService = flag }) with { CoreVerified = true }
                            : null,
                    };
                    var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new EnginePreferencesShell(), setup: new EnginePreferencesSetup());

                    Assert.Equal(pendingKind == "verified", coordinator.HasPending);
                    Assert.Equal(
                        pending is not null ? pendingService : installedService ?? true,
                        coordinator.UsesService);
                    Exception? required = Record.Exception(() => coordinator.RequirePending());
                    if (pendingKind == "verified")
                    {
                        Assert.Null(required);
                    }
                    else
                    {
                        Assert.Equal(
                            pending is null ? "installer.preferences.pending_missing" : "installer.preferences.core_verification_required",
                            Assert.IsType<InstallerProtocolException>(required).DiagnosticCode);
                    }
                }
            }
        }
    }

    [Fact]
    public void SeedCoversExactlyTheSelectedPreferencesAndNeverGrantsAgentProxyControl()
    {
        foreach (InstallerNotificationPreset notification in Enum.GetValues<InstallerNotificationPreset>())
        {
            for (int bits = 0; bits < 16; bits++)
            {
                var preferences = new InstallerPreferences
                {
                    LaunchAtStartup = (bits & 1) != 0,
                    InstallTransparentProxyService = (bits & 2) != 0,
                    EnableMcpServer = (bits & 4) != 0,
                    FollowSystemDefaults = (bits & 8) != 0,
                    Notifications = notification,
                };

                IReadOnlyDictionary<string, object> seed = WindowsInstallerPreferencesCoordinator.BuildSeed(preferences);

                Assert.Equal(preferences.LaunchAtStartup, seed[SettingsRegistry.Keys.LaunchAtStartupEnabled.Value]);
                Assert.Equal(preferences.InstallTransparentProxyService, seed[SettingsRegistry.Keys.TransparentProxyEnabled.Value]);
                Assert.Equal(preferences.EnableMcpServer, seed[SettingsRegistry.Keys.McpServerEnabled.Value]);
                Assert.Equal(notification != InstallerNotificationPreset.Disabled, seed[SettingsRegistry.Keys.NotificationEnabled.Value]);
                Assert.Equal(1, seed[SettingsRegistry.Keys.McpAllowedScopes.Value]);
                Assert.Equal(
                    notification switch
                    {
                        InstallerNotificationPreset.CriticalOnly => (int)NotificationLevel.CriticalOnly,
                        InstallerNotificationPreset.All => (int)NotificationLevel.More,
                        _ => (int)NotificationLevel.Default,
                    },
                    seed[SettingsRegistry.Keys.NotificationLevel.Value]);
                Assert.All(seed.Keys, key => Assert.NotNull(SettingsRegistry.Default.Get(key)));
                Assert.All(seed, pair => Assert.False(SettingsRegistry.Default.Get(pair.Key).IsSensitive, pair.Key));
            }
        }
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);

    private static WindowsInstallerPreferencesDocument Document(WindowsPayloadFixture fixture, InstallerPreferences preferences) => new(
        1,
        Guid.NewGuid().ToString("N"),
        fixture.Manifest.PackageIdentity.PackageFamilyName,
        fixture.Manifest.ExpectedPackageVersion,
        fixture.Manifest.InstallerPayloadSha256,
        preferences);
}
