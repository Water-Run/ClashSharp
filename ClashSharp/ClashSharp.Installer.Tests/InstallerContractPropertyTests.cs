using System.Globalization;
using System.Text.RegularExpressions;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Retirement;

namespace ClashSharp.Installer.Tests;

/// <summary>Grammar, policy and value-object contracts checked against independent oracles.</summary>
public sealed partial class InstallerContractPropertyTests
{
    private const int Seed = 20261109;

    // ---- protocol grammar vs an independent regular-language oracle --------------------------------

    [Fact]
    public void PackageVersionGrammarMatchesItsDefinitionOnGeneratedInputs()
    {
        Random random = new(Seed);
        string[] atoms = ["0", "1", "9", "10", "65535", "65536", "00", "01", "-1", "+1", "", " ", "a", "١", "1_0", "4294967296", "1.5"];
        for (int attempt = 0; attempt < 20000; attempt++)
        {
            string candidate = string.Join('.', Enumerable.Range(0, random.Next(0, 7)).Select(_ => atoms[random.Next(atoms.Length)]));
            bool expected = VersionOracle().IsMatch(candidate) && candidate.Split('.').All(static part => int.Parse(part, CultureInfo.InvariantCulture) <= 65535);

            Assert.Equal(expected, Accepts(() => InstallerProtocolValidation.ValidatePackageVersion(candidate)));
            if (expected)
            {
                Assert.Equal(Version.Parse(candidate), InstallerProtocolValidation.ParsePackageVersion(candidate));
            }
        }
    }

    [Fact]
    public void SidGrammarMatchesItsDefinitionOnGeneratedInputs()
    {
        Random random = new(Seed + 1);
        string[] atoms = ["S", "s", "1", "5", "21", "0", "00", "18", "32", "544", "4294967295", "4294967296", "281474976710655", "281474976710656", "-", "", " ", "x", "١"];
        string[] known = ["S-1-1-0", "S-1-5-2", "S-1-5-7", "S-1-5-18", "S-1-5-32-544"];
        int accepted = 0;
        for (int attempt = 0; attempt < 30000; attempt++)
        {
            string candidate = attempt % 3 == 0
                ? "S-1-5-21-" + string.Join('-', Enumerable.Range(0, random.Next(0, 17)).Select(_ => atoms[random.Next(atoms.Length)]))
                : string.Join('-', Enumerable.Range(0, random.Next(0, 22)).Select(_ => atoms[random.Next(atoms.Length)]));
            bool expected = SidOracle(candidate) && !known.Contains(candidate, StringComparer.Ordinal);

            Assert.Equal(expected, Accepts(() => InstallerProtocolValidation.ValidateTargetSid(candidate)));
            accepted += expected ? 1 : 0;
        }

        Assert.True(accepted > 100, "The generator must exercise accepted SIDs too.");
        foreach (string sid in known)
        {
            Assert.False(Accepts(() => InstallerProtocolValidation.ValidateTargetSid(sid)));
        }
    }

    [Fact]
    public void HexValidatorsAcceptExactlyTheirCanonicalCaseAndLength()
    {
        Random random = new(Seed + 2);
        const string alphabet = "0123456789abcdefABCDEFgG \u0660-\n";
        int lowerAccepted = 0;
        int upperAccepted = 0;
        for (int attempt = 0; attempt < 20000; attempt++)
        {
            // Start from a valid value of one of the two shapes, then damage it a little.
            bool lowerShape = random.Next(2) == 0;
            char[] chars = Enumerable.Range(0, lowerShape ? 64 : 40)
                .Select(_ => (lowerShape ? "0123456789abcdef" : "0123456789ABCDEF")[random.Next(16)])
                .ToArray();
            string candidate = new(chars);
            for (int edit = random.Next(0, 3); edit > 0 && candidate.Length > 0; edit--)
            {
                int at = random.Next(candidate.Length);
                switch (random.Next(3))
                {
                    case 0:
                        candidate = candidate.Remove(at, 1);
                        break;
                    case 1:
                        candidate = candidate.Insert(at, alphabet[random.Next(alphabet.Length)].ToString());
                        break;
                    default:
                        candidate = string.Concat(candidate.AsSpan(0, at), alphabet[random.Next(alphabet.Length)].ToString(), candidate.AsSpan(at + 1));
                        break;
                }
            }

            bool lower = candidate.Length == 64 && candidate.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
            bool upper = candidate.Length == 40 && candidate.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

            Assert.Equal(lower, Accepts(() => InstallerProtocolValidation.ValidateLowerHex256(candidate, "installer.test.hash")));
            Assert.Equal(upper, Accepts(() => InstallerProtocolValidation.ValidateUpperHex160(candidate, "installer.test.thumbprint")));
            lowerAccepted += lower ? 1 : 0;
            upperAccepted += upper ? 1 : 0;
        }

        Assert.True(lowerAccepted > 500 && upperAccepted > 500, "The generator must exercise accepted values too.");
        Assert.Equal("installer.test.hash", Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateLowerHex256("x", "installer.test.hash")).DiagnosticCode);
        Assert.Throws<ArgumentNullException>(() => InstallerProtocolValidation.ValidateLowerHex256(null!, "installer.test.hash"));
        Assert.Throws<ArgumentException>(() => InstallerProtocolValidation.ValidateLowerHex256(InstallerTestData.Hash, " "));
        Assert.Throws<ArgumentException>(() => InstallerProtocolValidation.ValidateUpperHex160(InstallerTestData.CertificateThumbprint, ""));
    }

    [Fact]
    public void DiagnosticCodeGrammarMatchesItsDefinitionOnGeneratedInputs()
    {
        Random random = new(Seed + 3);
        const string alphabet = "abcxyz019_._.-A \né";
        int acceptedCount = 0;
        for (int attempt = 0; attempt < 40000; attempt++)
        {
            int length = random.Next(5) == 0 ? random.Next(120, 135) : random.Next(0, 12);
            string candidate = new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            bool expected = length is >= 3 and <= 128
                && candidate[0] != '.' && candidate[^1] != '.'
                && !candidate.Contains("..", StringComparison.Ordinal)
                && candidate.All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.');

            Assert.Equal(expected, Accepts(() => InstallerProtocolValidation.ValidateDiagnosticCode(candidate)));
            acceptedCount += expected ? 1 : 0;
        }

        Assert.True(acceptedCount > 200, "The generator must exercise accepted codes too.");
        Assert.True(Accepts(() => InstallerProtocolValidation.ValidateDiagnosticCode(new string('a', 128))));
        Assert.False(Accepts(() => InstallerProtocolValidation.ValidateDiagnosticCode(new string('a', 129))));
        Assert.False(Accepts(() => InstallerProtocolValidation.ValidateDiagnosticCode("ab")));
    }

    // ---- request and value objects -------------------------------------------------------------------

    [Fact]
    public void RequestValidationAcceptsExactlyTheDocumentedOperationReassociationServiceMatrix()
    {
        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
        {
            foreach (bool reassociation in new[] { false, true })
            {
                foreach (bool service in new[] { false, true })
                {
                    InstallerRequest request = new(operation, InstallerTestData.Sid, reassociation, InstallerTestData.Version, InstallerTestData.Hash)
                    {
                        InstallTransparentProxyService = service,
                    };
                    bool expected = (!reassociation || operation == InstallerOperation.Repair) && (service || !reassociation);

                    Assert.Equal(expected, Accepts(request.Validate));
                }
            }
        }

        InstallerRequest undefined = InstallerTestData.Request() with { Operation = (InstallerOperation)99 };
        Assert.Equal("installer.request.operation_invalid", Assert.Throws<InstallerProtocolException>(undefined.Validate).DiagnosticCode);
        Assert.Equal(
            "installer.request.reassociation_invalid",
            Assert.Throws<InstallerProtocolException>((InstallerTestData.Request() with { AllowReassociation = true }).Validate).DiagnosticCode);
    }

    [Fact]
    public void PreferencesDefaultToTheRecommendedChoicesAreImmutableAndValidateTheirEnum()
    {
        InstallerPreferences defaults = InstallerPreferences.Default;

        Assert.True(defaults.CreateDesktopShortcut);
        Assert.False(defaults.RequestTaskbarPin);
        Assert.False(defaults.LaunchAtStartup);
        Assert.True(defaults.RegisterProxyRecovery);
        Assert.True(defaults.InstallTransparentProxyService);
        Assert.True(defaults.FollowSystemDefaults);
        Assert.False(defaults.EnableMcpServer);
        Assert.Equal(InstallerNotificationPreset.Default, defaults.Notifications);
        Assert.Same(InstallerPreferences.Default, InstallerPreferences.Default);
        Assert.Equal(defaults, new InstallerPreferences());
        defaults.Validate();

        InstallerPreferences changed = defaults with { LaunchAtStartup = true, EnableMcpServer = true };
        Assert.False(InstallerPreferences.Default.LaunchAtStartup);
        Assert.NotEqual(defaults, changed);
        Assert.Equal(changed, changed with { });

        foreach (InstallerNotificationPreset preset in Enum.GetValues<InstallerNotificationPreset>())
        {
            (defaults with { Notifications = preset }).Validate();
        }

        foreach (int raw in new[] { -1, 4, 99, int.MinValue, int.MaxValue })
        {
            InstallerProtocolException refusal = Assert.Throws<InstallerProtocolException>(
                (defaults with { Notifications = (InstallerNotificationPreset)raw }).Validate);
            Assert.Equal("installer.preferences.notifications_invalid", refusal.DiagnosticCode);
        }
    }

    [Fact]
    public void PreferencesWarningsAreIndependentBitsSoAnyCombinationRoundTrips()
    {
        InstallerPreferencesWarnings[] bits = Enum.GetValues<InstallerPreferencesWarnings>()
            .Where(static value => value != InstallerPreferencesWarnings.None)
            .ToArray();
        Assert.All(bits, static bit => Assert.Equal(1, int.PopCount((int)bit)));
        Assert.Equal(bits.Length, bits.Distinct().Count());
        foreach (int mask in Enumerable.Range(0, 1 << bits.Length))
        {
            InstallerPreferencesWarnings combined = bits.Where((_, index) => (mask & (1 << index)) != 0)
                .Aggregate(InstallerPreferencesWarnings.None, static (all, bit) => all | bit);
            InstallerExecutionResult result = new(InstallerExecutionOutcome.Succeeded, "installer.completed", null, false)
            {
                PreferencesWarnings = combined,
            };

            Assert.Equal(combined, result.PreferencesWarnings);
            Assert.Equal(bits.Count(bit => combined.HasFlag(bit)), int.PopCount(mask));
        }
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void ProgressRejectsPercentsOutsideZeroToOneHundred(int percent, bool valid)
    {
        foreach (InstallerTransactionPhase phase in Enum.GetValues<InstallerTransactionPhase>())
        {
            bool accepted = true;
            try
            {
                _ = InstallerProgress.Create(phase, percent, "installer.progress.x");
            }
            catch (ArgumentOutOfRangeException)
            {
                accepted = false;
            }

            Assert.Equal(valid, accepted);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => InstallerProgress.Create((InstallerTransactionPhase)99, 5, "installer.progress.x"));
        Assert.ThrowsAny<ArgumentException>(() => InstallerProgress.Create(InstallerTransactionPhase.Prepared, 5, " "));
        Assert.ThrowsAny<ArgumentException>(() => InstallerProgress.Create(InstallerTransactionPhase.Prepared, 5, null!));
    }

    [Fact]
    public void ProcessFatalClassificationFollowsEveryPathThroughRandomExceptionGraphs()
    {
        Random random = new(Seed + 4);
        for (int attempt = 0; attempt < 3000; attempt++)
        {
            List<Node> nodes = [];
            int count = random.Next(1, 12);
            for (int index = 0; index < count; index++)
            {
                nodes.Add(new Node { Fatal = random.Next(8) == 0, Aggregate = random.Next(3) == 0 });
            }

            for (int index = 0; index < count; index++)
            {
                int children = nodes[index].Aggregate ? random.Next(0, 4) : random.Next(0, 2);
                for (int child = 0; child < children; child++)
                {
                    // Forward edges only: .NET exception inner chains cannot be cyclic.
                    if (index + 1 < count)
                    {
                        nodes[index].Children.Add(random.Next(index + 1, count));
                    }
                }
            }

            Exception[] built = new Exception[count];
            for (int index = count - 1; index >= 0; index--)
            {
                Node node = nodes[index];
                Exception[] inner = node.Children.Select(child => built[child]).ToArray();
                Exception self = node.Fatal
                    ? InstallerFatalFailureTests.Fatal("memory")
                    : node.Aggregate
                        ? new AggregateException("aggregate", inner.Length == 0 ? [new InvalidOperationException()] : inner)
                        : new InvalidOperationException("ordinary", inner.FirstOrDefault());
                built[index] = self;
            }

            bool expected = Reaches(nodes, 0);

            Assert.Equal(expected, InstallerExceptionPolicy.IsProcessFatal(built[0]));
        }

        Assert.Throws<ArgumentNullException>(() => InstallerExceptionPolicy.IsProcessFatal(null!));
    }

    [Fact]
    public void ProcessFatalClassificationNeverRecursesOnTheStackAndSurvivesSharedNodes()
    {
        Exception chain = new IOException("leaf");
        for (int index = 0; index < 20000; index++)
        {
            chain = new InvalidOperationException("link", chain);
        }

        Assert.False(InstallerExceptionPolicy.IsProcessFatal(chain));
        Assert.True(InstallerExceptionPolicy.IsProcessFatal(new InvalidOperationException("wrap", InstallerFatalFailureTests.Fatal("memory"))));

        Exception shared = new IOException("shared");
        AggregateException diamond = new(new AggregateException(shared), new AggregateException(shared), new AggregateException(InstallerFatalFailureTests.Fatal("memory")));
        Assert.True(InstallerExceptionPolicy.IsProcessFatal(diamond));
        Assert.False(InstallerExceptionPolicy.IsProcessFatal(new AggregateException(new AggregateException(shared), new AggregateException(shared))));
        Assert.True(InstallerExceptionPolicy.IsProcessFatal(new OperationCanceledException("c", InstallerFatalFailureTests.Fatal("memory"))));
        Assert.True(InstallerExceptionPolicy.IsProcessFatal(InstallerFatalFailureTests.Fatal("stack")));
        Assert.True(InstallerExceptionPolicy.IsProcessFatal(InstallerFatalFailureTests.Fatal("access")));
        Assert.True(InstallerExceptionPolicy.IsProcessFatal(InstallerFatalFailureTests.Fatal("domain")));
        Assert.False(InstallerExceptionPolicy.IsProcessFatal(new OperationCanceledException()));
    }

    // ---- platform policy -----------------------------------------------------------------------------

    [Fact]
    public void PlatformPolicySupportsExactlyNativeX64ClientOrServerDesktopAtOrAboveItsBuildFloors()
    {
        InstallerCpuArchitecture[] architectures = Enum.GetValues<InstallerCpuArchitecture>()
            .Append((InstallerCpuArchitecture)42)
            .ToArray();
        int[] builds =
        [
            int.MinValue, -1, 0, 1, InstallerPlatformPolicy.MinimumWindowsBuild - 1, InstallerPlatformPolicy.MinimumWindowsBuild,
            InstallerPlatformPolicy.MinimumWindowsBuild + 1, InstallerPlatformPolicy.MinimumWindowsServerBuild - 1,
            InstallerPlatformPolicy.MinimumWindowsServerBuild, InstallerPlatformPolicy.MinimumWindowsServerBuild + 1, int.MaxValue,
        ];
        int supported = 0;
        int cases = 0;
        foreach (bool windows in new[] { false, true })
        {
            foreach (bool workstation in new[] { false, true })
            {
                foreach (bool serverDesktop in new[] { false, true })
                {
                    foreach (int build in builds)
                    {
                        foreach (InstallerCpuArchitecture os in architectures)
                        {
                            foreach (InstallerCpuArchitecture process in architectures)
                            {
                                InstallerPlatformAssessment actual = InstallerPlatformPolicy.Evaluate(
                                    new InstallerPlatformFacts(windows, workstation, build, os, process, serverDesktop));
                                (bool isSupported, string code) = Expected(windows, workstation, serverDesktop, build, os, process);

                                bool declarative = windows
                                    && os == InstallerCpuArchitecture.X64
                                    && process == InstallerCpuArchitecture.X64
                                    && (workstation
                                        ? build >= InstallerPlatformPolicy.MinimumWindowsBuild
                                        : serverDesktop && build >= InstallerPlatformPolicy.MinimumWindowsServerBuild);

                                Assert.Equal(declarative, actual.IsSupported);
                                Assert.Equal(isSupported, actual.IsSupported);
                                Assert.Equal(code, actual.DiagnosticCode);
                                supported += actual.IsSupported ? 1 : 0;
                                cases++;
                            }
                        }
                    }
                }
            }
        }

        Assert.True(supported > 0 && supported < cases);
        Assert.Throws<ArgumentNullException>(() => InstallerPlatformPolicy.Evaluate(null!));
    }

    // ---- owner-transfer and retired-uninstall launch identities ----------------------------------------

    [Fact]
    public void OwnerTransferRequestsBindOnlyInstallOrRepairAndTheirPublicFields()
    {
        InstallerOwnerTransferRequest valid = new(InstallerTestData.Hash, InstallerOperation.Install, InstallerTestData.Version, InstallerTestData.OtherHash);
        valid.Validate();
        InstallerRequest mapped = valid.ForAuthenticatedAccount(InstallerTestData.Sid);

        Assert.Equal(InstallerOperation.Install, mapped.Operation);
        Assert.Equal(InstallerTestData.Sid, mapped.TargetSid);
        Assert.False(mapped.AllowReassociation);
        Assert.Equal(valid.ExpectedPackageVersion, mapped.ExpectedPackageVersion);
        Assert.Equal(valid.InstallerPayloadSha256, mapped.InstallerPayloadSha256);
        Assert.True(mapped.InstallTransparentProxyService);
        Assert.Equal(InstallerOperation.Repair, (valid with { Operation = InstallerOperation.Repair }).ForAuthenticatedAccount(InstallerTestData.Sid).Operation);

        InstallerOwnerTransferRequest[] invalid =
        [
            valid with { Operation = InstallerOperation.Uninstall },
            valid with { Operation = (InstallerOperation)9 },
            valid with { SessionId = "short" },
            valid with { SessionId = InstallerTestData.Hash.ToUpperInvariant() },
            valid with { ExpectedPackageVersion = "1.2.3" },
            valid with { InstallerPayloadSha256 = InstallerTestData.Hash[..63] },
        ];
        Assert.All(invalid, request => Assert.Throws<InstallerProtocolException>(request.Validate));
        Assert.All(invalid, request => Assert.Throws<InstallerProtocolException>(() => request.ForAuthenticatedAccount(InstallerTestData.Sid)));
        Assert.Throws<InstallerProtocolException>(() => valid.ForAuthenticatedAccount("S-1-5-18"));
        Assert.Throws<InstallerProtocolException>(() => valid.ForAuthenticatedAccount(""));
    }

    [Fact]
    public void OwnerTransferRequestMustBelongToTheLaunchThatCarriesIt()
    {
        InstallerOwnerTransferBootstrap bootstrap = InstallerOwnerTransferBootstrap.Create(InstallerTestData.OtherHash, parentProcessId: 4242);
        InstallerOwnerTransferRequest matching = new(bootstrap.SessionId, InstallerOperation.Repair, InstallerTestData.Version, bootstrap.InstallerPayloadSha256);

        matching.ValidateAgainst(bootstrap);

        foreach (InstallerOwnerTransferRequest mismatch in new[]
        {
            matching with { SessionId = InstallerTestData.Hash },
            matching with { InstallerPayloadSha256 = InstallerTestData.Hash },
        })
        {
            Assert.Equal("installer.owner_transfer.bootstrap_mismatch", Assert.Throws<InstallerProtocolException>(() => mismatch.ValidateAgainst(bootstrap)).DiagnosticCode);
        }

        Assert.Throws<ArgumentNullException>(() => matching.ValidateAgainst(null!));
        Assert.Throws<InstallerProtocolException>(() => (matching with { Operation = InstallerOperation.Uninstall }).ValidateAgainst(bootstrap));
        Assert.Throws<InstallerProtocolException>(() => matching.ValidateAgainst(bootstrap with { ParentProcessId = 0 }));
    }

    [Fact]
    public void OwnerTransferOffersAndDecisionsBindSessionOfferAndBothDistinctAccounts()
    {
        InstallerOwnerTransferRequest request = new(InstallerTestData.Hash, InstallerOperation.Install, InstallerTestData.Version, InstallerTestData.OtherHash);
        InstallerOwnerTransferOffer offer = new(request, InstallerTestData.TransactionId, InstallerOwnerTransferJournalTests.PreviousSid, InstallerTestData.Sid, IsRecovery: false);
        offer.Validate();
        offer.ValidateAgainst(request, InstallerTestData.Sid);

        Assert.Equal("installer.owner_transfer.identity_conflict", Assert.Throws<InstallerProtocolException>(
            (offer with { PreviousOwnerSid = InstallerTestData.Sid }).Validate).DiagnosticCode);
        foreach (InstallerOwnerTransferOffer broken in new[]
        {
            offer with { OfferId = "x" },
            offer with { PreviousOwnerSid = "S-1-5-18" },
            offer with { NextOwnerSid = "nonsense" },
            offer with { Request = request with { SessionId = "x" } },
        })
        {
            Assert.Throws<InstallerProtocolException>(broken.Validate);
        }

        // The offered target must be the authenticated account and the candidate must be the one requested.
        Assert.Equal("installer.owner_transfer.offer_mismatch", Assert.Throws<InstallerProtocolException>(
            () => offer.ValidateAgainst(request, InstallerOwnerTransferJournalTests.PreviousSid)).DiagnosticCode);
        foreach (InstallerOwnerTransferRequest other in new[]
        {
            request with { SessionId = InstallerTestData.OtherHash },
            request with { ExpectedPackageVersion = "9.9.9.9" },
            request with { InstallerPayloadSha256 = InstallerTestData.Hash },
            request with { Operation = InstallerOperation.Repair },
        })
        {
            Assert.Throws<InstallerProtocolException>(() => offer.ValidateAgainst(other, InstallerTestData.Sid));
        }

        // A recovery offer may carry the durable operation, not the one the user clicked now.
        InstallerOwnerTransferOffer recovery = offer with { IsRecovery = true, Request = request with { Operation = InstallerOperation.Repair } };
        recovery.ValidateAgainst(request, InstallerTestData.Sid);
        Assert.Throws<InstallerProtocolException>(() =>
            (recovery with { Request = request with { Operation = InstallerOperation.Repair, SessionId = InstallerTestData.OtherHash } })
                .ValidateAgainst(request, InstallerTestData.Sid));

        Assert.DoesNotContain(InstallerTestData.Sid, offer.ToString(), StringComparison.Ordinal);
        InstallerOwnerTransferDecision decision = new(request.SessionId, offer.OfferId, Accepted: true);
        decision.ValidateAgainst(offer);
        Assert.Equal("installer.owner_transfer.decision_mismatch", Assert.Throws<InstallerProtocolException>(
            () => (decision with { SessionId = InstallerTestData.OtherHash }).ValidateAgainst(offer)).DiagnosticCode);
        Assert.Throws<InstallerProtocolException>(() => (decision with { OfferId = InstallerTestData.Hash }).ValidateAgainst(offer));
        // A refusal is as binding as an acceptance: it cannot be replayed into another inspection either.
        (decision with { Accepted = false }).ValidateAgainst(offer);
        Assert.Throws<InstallerProtocolException>(() => (decision with { Accepted = false, OfferId = InstallerTestData.Hash }).ValidateAgainst(offer));
        Assert.Throws<ArgumentNullException>(() => decision.ValidateAgainst(null!));
    }

    [Theory]
    [InlineData(typeof(InstallerOwnerTransferBootstrap))]
    [InlineData(typeof(InstallerRetiredUninstallBootstrap))]
    public void HelperBootstrapsRoundTripExactlyAndRefuseEveryDeviationOrMisplacedReservedFlag(Type kind)
    {
        Bootstrap bootstrap = Bootstrap.Create(kind, InstallerTestData.OtherHash, 4242);
        bootstrap.Validate();
        IReadOnlyList<string> arguments = bootstrap.ToArguments();

        Assert.Equal(7, arguments.Count);
        Assert.Equal(bootstrap.SessionId, arguments[2]);
        Assert.Equal(bootstrap.Payload, arguments[4]);
        Assert.Equal("4242", arguments[6]);
        Bootstrap parsed = Bootstrap.Parse(kind, arguments);
        Assert.Equal(bootstrap.SessionId, parsed.SessionId);
        Assert.Equal(bootstrap.Payload, parsed.Payload);
        Assert.Equal(bootstrap.ParentProcessId, parsed.ParentProcessId);
        Assert.Equal(bootstrap.PipeName, parsed.PipeName);
        Assert.Equal(arguments, parsed.ToArguments());
        Assert.StartsWith("ClashSharp.Installer.", bootstrap.PipeName, StringComparison.Ordinal);
        Assert.Contains(bootstrap.SessionId, bootstrap.PipeName, StringComparison.Ordinal);

        for (int index = 0; index < arguments.Count; index++)
        {
            // Even without the mode flag, the remaining reserved flags are misplaced and refused.
            string[] removed = [.. arguments.Where((_, i) => i != index)];
            Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, removed));

            Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, [.. arguments, "extra"]));
        }

        foreach (string pid in new[] { "", "0", "-1", "+4242", "04242", " 4242", "4242 ", "4242.0", "2147483648", "9999999999", "1e3", "٤٢", "0x10" })
        {
            string[] mutated = [.. arguments];
            mutated[6] = pid;
            Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, mutated));
        }

        string[] maximum = [.. arguments];
        maximum[6] = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(int.MaxValue, Bootstrap.Parse(kind, maximum).ParentProcessId);
        string[] upper = [.. arguments];
        upper[2] = upper[2].ToUpperInvariant();
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, upper));
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, [.. arguments.Reverse()]));
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, [.. arguments, null!]));
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, ["--normal", arguments[0]]));
        Assert.Null(Bootstrap.TryParse(kind, []));
        Assert.Null(Bootstrap.TryParse(kind, ["--normal", "value"]));
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Parse(kind, arguments.Select(argument => argument == arguments[0] ? argument.ToUpperInvariant() : argument).ToArray()));
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Create(kind, "x", 4242));
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Create(kind, InstallerTestData.OtherHash, 0));
        Assert.Throws<InstallerProtocolException>(() => Bootstrap.Create(kind, InstallerTestData.OtherHash, -5));
        Assert.NotEqual(Bootstrap.Create(kind, InstallerTestData.OtherHash, 4242).SessionId, Bootstrap.Create(kind, InstallerTestData.OtherHash, 4242).SessionId);
    }

    [Fact]
    public void TheTwoHelperLaunchGrammarsNeverAcceptEachOthersArgumentsAndTheirPipesNeverCollide()
    {
        InstallerOwnerTransferBootstrap transfer = InstallerOwnerTransferBootstrap.Create(InstallerTestData.Hash, 77);
        InstallerRetiredUninstallBootstrap retired = InstallerRetiredUninstallBootstrap.Create(InstallerTestData.Hash, 77);

        // Each parser treats the other mode's vector as "not my launch" and never yields a bootstrap from it.
        Assert.Null(InstallerRetiredUninstallBootstrap.Parse(transfer.ToArguments()));
        Assert.Null(InstallerOwnerTransferBootstrap.Parse(retired.ToArguments()));
        Assert.NotEqual(transfer.BuildSessionPipeName(), retired.BuildSessionPipeName());
        Assert.DoesNotContain("RetiredUninstall", transfer.BuildSessionPipeName(), StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerTransfer", retired.BuildSessionPipeName(), StringComparison.Ordinal);
        Assert.Throws<InstallerProtocolException>(() => (transfer with { ParentProcessId = 0 }).ToArguments());
        Assert.Throws<InstallerProtocolException>(() => (retired with { SessionId = "x" }).BuildSessionPipeName());
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static (bool Supported, string Code) Expected(
        bool windows,
        bool workstation,
        bool serverDesktop,
        int build,
        InstallerCpuArchitecture os,
        InstallerCpuArchitecture process)
    {
        if (!windows)
        {
            return (false, "installer.environment.windows_required");
        }

        if (!workstation && !serverDesktop)
        {
            return (false, "installer.environment.desktop_experience_required");
        }

        if (workstation && build < 22000)
        {
            return (false, "installer.environment.windows_11_required");
        }

        if (!workstation && build < 26100)
        {
            return (false, "installer.environment.windows_server_2025_required");
        }

        if (os != InstallerCpuArchitecture.X64)
        {
            return (false, "installer.environment.x64_os_required");
        }

        return process != InstallerCpuArchitecture.X64
            ? (false, "installer.environment.x64_process_required")
            : (true, "installer.environment.supported");
    }

    private static bool Accepts(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (InstallerProtocolException)
        {
            return false;
        }
    }

    private static bool Accepts<T>(Func<T> action) => Accepts(() => _ = action());

    private static bool SidOracle(string value)
    {
        if (value.Length is < 7 or > 184)
        {
            return false;
        }

        string[] parts = value.Split('-');
        if (parts.Length is < 4 or > 18 || parts[0] != "S")
        {
            return false;
        }

        foreach (string part in parts.Skip(1))
        {
            if (!NumberOracle().IsMatch(part) || !ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return parts[1] == "1"
            && ulong.Parse(parts[2], CultureInfo.InvariantCulture) <= 0x0000_ffff_ffff_ffffUL
            && parts.Skip(3).All(static part => uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static bool Reaches(List<Node> nodes, int index)
    {
        Node node = nodes[index];
        return node.Fatal || node.Children.Any(child => Reaches(nodes, child));
    }

    [GeneratedRegex("^(0|[1-9][0-9]*)\\z")]
    private static partial Regex NumberOracle();

    [GeneratedRegex("^(0|[1-9][0-9]{0,4})(\\.(0|[1-9][0-9]{0,4})){3}\\z")]
    private static partial Regex VersionOracle();

    private sealed class Node
    {
        internal bool Fatal { get; init; }

        internal bool Aggregate { get; init; }

        internal List<int> Children { get; } = [];
    }

    /// <summary>Uniform view over the two sibling helper-launch records.</summary>
    private sealed class Bootstrap(
        string sessionId,
        string payload,
        int parentProcessId,
        IReadOnlyList<string> arguments,
        string pipeName,
        Action validate)
    {
        internal string SessionId { get; } = sessionId;

        internal string Payload { get; } = payload;

        internal int ParentProcessId { get; } = parentProcessId;

        internal string PipeName { get; } = pipeName;

        internal void Validate() => validate();

        internal IReadOnlyList<string> ToArguments() => arguments;

        internal static Bootstrap Create(Type kind, string payload, int pid) => kind == typeof(InstallerOwnerTransferBootstrap)
            ? Of(InstallerOwnerTransferBootstrap.Create(payload, pid))
            : Of(InstallerRetiredUninstallBootstrap.Create(payload, pid));

        internal static Bootstrap Parse(Type kind, IReadOnlyList<string> arguments) =>
            TryParse(kind, arguments) ?? throw new InvalidOperationException("The vector was not a helper launch.");

        internal static Bootstrap? TryParse(Type kind, IReadOnlyList<string> arguments) => kind == typeof(InstallerOwnerTransferBootstrap)
            ? InstallerOwnerTransferBootstrap.Parse(arguments) is { } transfer ? Of(transfer) : null
            : InstallerRetiredUninstallBootstrap.Parse(arguments) is { } retired ? Of(retired) : null;

        private static Bootstrap Of(InstallerOwnerTransferBootstrap value) =>
            new(value.SessionId, value.InstallerPayloadSha256, value.ParentProcessId, value.ToArguments(), value.BuildSessionPipeName(), value.Validate);

        private static Bootstrap Of(InstallerRetiredUninstallBootstrap value) =>
            new(value.SessionId, value.InstallerPayloadSha256, value.ParentProcessId, value.ToArguments(), value.BuildSessionPipeName(), value.Validate);
    }
}
