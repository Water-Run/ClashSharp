using System.Globalization;
using System.Text.RegularExpressions;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

/// <summary>
/// The installer speaks every interface language of the app: each text it can show has one complete translation
/// row, every translation keeps the source's placeholders and the length limits the shell enforces, and no
/// installer flow leaves Chinese text on screen in another language.
/// </summary>
public sealed class InstallerLocalizationTests
{
    private static readonly Regex Han = new("[\\u4e00-\\u9fff]", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    public static TheoryData<int> NonChineseLanguages() => new(
        (int)InstallerLanguage.English, (int)InstallerLanguage.Russian, (int)InstallerLanguage.French,
        (int)InstallerLanguage.German, (int)InstallerLanguage.Persian, (int)InstallerLanguage.Korean);

    public static TheoryData<int> AllLanguages() => new(Enum.GetValues<InstallerLanguage>().Select(static language => (int)language));

    [Fact]
    public void EveryRowIsCompleteUniqueAndKeepsItsPlaceholders()
    {
        string[][] rows = InstallerTranslations.Rows.ToArray();
        Assert.Equal(rows.Length, rows.Select(static row => row[0]).Distinct(StringComparer.Ordinal).Count());
        foreach (string[] row in rows)
        {
            Assert.Equal(8, row.Length);
            string[] expected = Placeholder.Matches(row[0]).Select(static match => match.Value).Order(StringComparer.Ordinal).ToArray();
            for (int column = 1; column < row.Length; column++)
            {
                Assert.False(string.IsNullOrWhiteSpace(row[column]), $"empty translation for '{row[0]}'");
                Assert.Equal(expected, Placeholder.Matches(row[column]).Select(static match => match.Value).Order(StringComparer.Ordinal).ToArray());
                object[] arguments = Enumerable.Range(0, 4).Select(static index => (object)("v" + index)).ToArray();
                _ = string.Format(CultureInfo.InvariantCulture, row[column], arguments);
                if (column >= (int)InstallerLanguage.English)
                {
                    Assert.False(Han.IsMatch(row[column]), $"untranslated Chinese in column {column}: {row[column]}");
                }
            }
        }
    }

    /// <summary>
    /// Every text the installer sources can show, from the view model, the runtime, the platform guidance and
    /// the window markup, has a translation row; a new text without one fails here instead of showing Chinese
    /// to every other language.
    /// </summary>
    [Fact]
    public void EveryTextTheInstallerCanShowHasATranslationRow()
    {
        HashSet<string> rows = InstallerTranslations.Rows.Select(static row => row[0]).ToHashSet(StringComparer.Ordinal);
        List<string> missing = [];
        foreach (string source in ExtractSources())
        {
            if (!rows.Contains(source)) { missing.Add(source); }
        }

        Assert.True(missing.Count == 0, "Missing translation rows:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Theory]
    [InlineData("zh-CN", InstallerLanguage.SimplifiedChinese)]
    [InlineData("zh-SG", InstallerLanguage.SimplifiedChinese)]
    [InlineData("zh-Hans", InstallerLanguage.SimplifiedChinese)]
    [InlineData("zh-TW", InstallerLanguage.TraditionalChinese)]
    [InlineData("zh-HK", InstallerLanguage.TraditionalChinese)]
    [InlineData("zh-MO", InstallerLanguage.TraditionalChinese)]
    [InlineData("zh-Hant", InstallerLanguage.TraditionalChinese)]
    [InlineData("en-US", InstallerLanguage.English)]
    [InlineData("en-GB", InstallerLanguage.English)]
    [InlineData("ru-RU", InstallerLanguage.Russian)]
    [InlineData("fr-CA", InstallerLanguage.French)]
    [InlineData("de-AT", InstallerLanguage.German)]
    [InlineData("fa-IR", InstallerLanguage.Persian)]
    [InlineData("ko-KR", InstallerLanguage.Korean)]
    [InlineData("ja-JP", InstallerLanguage.English)]
    [InlineData("es-ES", InstallerLanguage.English)]
    [InlineData("", InstallerLanguage.English)]
    public void TheWindowsDisplayLanguageChoosesTheInstallerLanguage(string culture, InstallerLanguage expected)
    {
        Assert.Equal(expected, InstallerText.Detect(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void SentencesRunTogetherOnlyInChinese()
    {
        foreach (InstallerLanguage language in Enum.GetValues<InstallerLanguage>())
        {
            string joined = new InstallerText(language).JoinSentences("A.", null, "", "B.");
            bool chinese = language is InstallerLanguage.SimplifiedChinese or InstallerLanguage.TraditionalChinese;
            Assert.Equal(chinese ? "A.B." : "A. B.", joined);
        }
    }

    [Fact]
    public void OnlyPersianIsRightToLeftAndChangesAreAnnounced()
    {
        InstallerText text = new();
        List<string?> changed = [];
        text.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        int revision = text.Revision;

        text.Language = InstallerLanguage.Persian;
        Assert.True(text.IsRightToLeft);
        Assert.NotEqual(revision, text.Revision);
        Assert.Contains(nameof(InstallerText.Revision), changed);
        Assert.Contains(nameof(InstallerText.IsRightToLeft), changed);

        changed.Clear();
        text.Language = InstallerLanguage.Persian;
        Assert.Empty(changed);
        text.Language = InstallerLanguage.Korean;
        Assert.False(text.IsRightToLeft);
        Assert.Throws<ArgumentOutOfRangeException>(() => text.Language = (InstallerLanguage)99);
    }

    /// <summary>
    /// The readiness the runtime composes stays within the limits the shell accepts in every language, for
    /// every installed state and platform refusal; a translation that grew too long would otherwise surface as a
    /// protocol failure instead of an explanation.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLanguages))]
    public async Task RuntimeReadinessFitsTheShellLimitsInEveryLanguage(int languageValue)
    {
        InstallerText text = new((InstallerLanguage)languageValue);
        foreach (InstallerRuntimeInspection inspection in Inspections())
        {
            using ProductionInstallerRuntime runtime = new(new InspectionBackend(inspection), text);
            InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(CancellationToken.None);

            Assert.InRange(readiness.StatusTitle.Length, 1, 160);
            Assert.InRange(readiness.StatusDetail.Length, 1, 1_024);
            Assert.All(readiness.Capabilities, capability =>
            {
                Assert.InRange(capability.Name.Length, 1, 128);
                Assert.InRange(capability.Detail.Length, 1, 512);
            });
            if (languageValue >= (int)InstallerLanguage.English)
            {
                Assert.False(Han.IsMatch(readiness.StatusTitle + readiness.StatusDetail
                    + string.Concat(readiness.Capabilities.Select(static capability => capability.Name + capability.Detail))),
                    $"Chinese readiness text in {(InstallerLanguage)languageValue}: {readiness.StatusTitle} / {readiness.StatusDetail}");
            }
        }
    }

    /// <summary>
    /// Through inspection, every terminal outcome and every failure code the shell explains, the view model shows
    /// no Chinese text in a non-Chinese language.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonChineseLanguages))]
    public async Task NoInstallerFlowLeavesChineseOnScreen(int languageValue)
    {
        InstallerText text = new((InstallerLanguage)languageValue);
        foreach (InstallerExecutionResult result in Results())
        {
            InspectionBackend backend = new(Inspections().First()) { Result = result };
            using ProductionInstallerRuntime runtime = new(backend, text);
            using InstallerShellViewModel viewModel = new(runtime, "1.2.3.4", isInternalCandidate: true, text);
            await viewModel.InitializeAsync();
            AssertNoChinese(viewModel, "ready");

            await viewModel.PrimaryActionCommand.ExecuteAsync();
            AssertNoChinese(viewModel, result.DiagnosticCode);
        }
    }

    [Fact]
    public async Task TheLanguageCanChangeUntilTheFirstOperationAndThenStaysFixed()
    {
        InstallerText text = new();
        InspectionBackend backend = new(Inspections().First());
        using ProductionInstallerRuntime runtime = new(backend, text);
        using InstallerShellViewModel viewModel = new(runtime, text: text);
        await viewModel.InitializeAsync();
        Assert.Matches(Han, viewModel.StatusTitle);
        Assert.True(viewModel.CanChangeLanguage);
        Assert.Equal(InstallerText.LanguageNames.Count, viewModel.LanguageOptions.Count);

        viewModel.LanguageIndex = (int)InstallerLanguage.English;
        await WaitUntilIdle(viewModel);
        Assert.Equal(InstallerLanguage.English, text.Language);
        Assert.DoesNotMatch(Han, viewModel.StatusTitle + viewModel.StatusDetail + viewModel.PrimaryActionText);

        await viewModel.PrimaryActionCommand.ExecuteAsync();
        Assert.False(viewModel.CanChangeLanguage);
        viewModel.LanguageIndex = (int)InstallerLanguage.SimplifiedChinese;
        Assert.Equal(InstallerLanguage.English, text.Language);
        Assert.Equal((int)InstallerLanguage.English, viewModel.LanguageIndex);
    }

    private static async Task WaitUntilIdle(InstallerShellViewModel viewModel)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeouts.Completion;
        while (viewModel.IsBusy && DateTime.UtcNow < deadline) { await Task.Delay(10); }
        Assert.False(viewModel.IsBusy);
    }

    private static void AssertNoChinese(InstallerShellViewModel viewModel, string stage)
    {
        string[] visible =
        [
            viewModel.StatusTitle, viewModel.StatusDetail, viewModel.StatusBadge, viewModel.ProgressStatus,
            viewModel.PrimaryActionText, viewModel.SecondaryActionText, viewModel.PrimaryActionAutomationName,
            viewModel.SecondaryActionAutomationName, viewModel.ReleaseChannelText, viewModel.NotificationPresetDescription,
            viewModel.TransparentProxyServiceDescription, viewModel.McpServerDescription, viewModel.SystemDefaultsDescription,
            .. viewModel.NotificationPresetOptions,
            .. viewModel.SystemDefaultsDetails.SelectMany(static detail => new[] { detail.Label, detail.Value }),
        ];
        foreach (string text in visible)
        {
            Assert.False(Han.IsMatch(text), $"{stage}: Chinese text on screen: {text}");
        }
    }

    private static IEnumerable<InstallerRuntimeInspection> Inspections()
    {
        yield return new(new InstallerEnvironmentSnapshot(true, null, false, null), null, "1.2.3.4");
        yield return new(new InstallerEnvironmentSnapshot(true, "1.2.3.4", false, null), null, "1.2.3.4");
        yield return new(new InstallerEnvironmentSnapshot(true, "1.2.3.3", false, null), null, "1.2.3.4");
        yield return new(new InstallerEnvironmentSnapshot(true, "1.2.3.5", false, null), null, "1.2.3.4");
        yield return new(new InstallerEnvironmentSnapshot(true, "1.2.3.4", true, null), null, "1.2.3.4");
        yield return new(new InstallerEnvironmentSnapshot(true, "1.2.3.4", false, null), null, "1.2.3.4") { PendingUserPreferences = true };
        foreach (string code in new[]
        {
            "installer.environment.windows_11_required", "installer.environment.windows_server_2025_required",
            "installer.environment.desktop_experience_required", "installer.environment.x64_os_required",
            "installer.environment.x64_process_required", "installer.environment.unknown_platform",
        })
        {
            yield return new(new InstallerEnvironmentSnapshot(false, null, false, code), null, "1.2.3.4");
            yield return new(new InstallerEnvironmentSnapshot(false, "1.2.3.4", false, code), null, "1.2.3.4");
        }
    }

    private static IEnumerable<InstallerExecutionResult> Results()
    {
        yield return new(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false);
        yield return new(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false)
        {
            PreferencesWarnings = InstallerPreferencesWarnings.UserSetupPending | InstallerPreferencesWarnings.StartupDenied
                | InstallerPreferencesWarnings.ProxyRecoveryDenied | InstallerPreferencesWarnings.ExistingSettingsPreserved
                | InstallerPreferencesWarnings.NativePinConfirmationPending,
        };
        string[] failureCodes =
        [
            "installer.runtime.execution_failed", "installer.package.content_conflict", "installer.package.downgrade_rejected",
            "installer.transaction.release_conflict", "installer.transaction.root_verification_failed",
            "installer.elevation.certificate_untrusted", "installer.elevation.certificate_expired",
            "installer.elevation.certificate_revoked", "installer.elevation.authenticode_missing",
            "installer.elevation.authenticode_invalid", "installer.elevation.signer_mismatch",
            "installer.recovery.original_unavailable", "installer.release.locked_file_hash_mismatch",
            "installer.release.payload_file_set_invalid", "installer.release.payload_directory_set_invalid",
            "installer.release.payload_lock_failed", "installer.release.payload_reparse_rejected",
            "installer.elevation.executable_lock_failed", "installer.concurrent_action_rejected",
            "installer.machine_helper.session_unavailable", "installer.unknown.failure",
        ];
        foreach (InstallerExecutionOutcome outcome in new[]
        {
            InstallerExecutionOutcome.Failed, InstallerExecutionOutcome.Blocked,
            InstallerExecutionOutcome.Uncertain, InstallerExecutionOutcome.Cancelled,
        })
        {
            foreach (string code in failureCodes)
            {
                bool blocked = outcome == InstallerExecutionOutcome.Blocked;
                yield return new(outcome, code, blocked ? null : InstallerTransactionPhase.Prepared, RecoveryPending: !blocked);
            }
        }
    }

    private static IEnumerable<string> ExtractSources()
    {
        string root = FindSourceRoot();
        Regex call = new("\\b(?:T|F|localized\\.Format|_text\\.Format)\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        Regex indexer = new("(?:localized|_text)\\[\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\]", RegexOptions.CultureInvariant);
        Regex literal = new("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        Regex markup = new("\\{l:T '([^']*)'\\}", RegexOptions.CultureInvariant);
        string presentation = Path.Combine(root, "ClashSharp.Installer.Presentation");
        string[] translatedWhereUsed =
        [
            Path.Combine(presentation, "Presentation", "InstallerShellViewModel.cs"),
            Path.Combine(presentation, "Runtime", "InstallerPlatformGuidance.cs"),
            Path.Combine(presentation, "Runtime", "IInstallerRuntime.cs"),
        ];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(presentation, "Presentation"), "InstallerShellViewModel*.cs")
            .Concat([Path.Combine(presentation, "Runtime", "ProductionInstallerRuntime.cs")]).Concat(translatedWhereUsed).Distinct())
        {
            string content = File.ReadAllText(file);
            IEnumerable<string> found = call.Matches(content).Concat(indexer.Matches(content)).Select(static match => match.Groups[1].Value);
            if (translatedWhereUsed.Contains(file))
            {
                found = found.Concat(content.Split('\n').Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    .SelectMany(line => literal.Matches(line).Select(static match => match.Groups[1].Value)));
            }

            foreach (string source in found.Where(source => Han.IsMatch(source)))
            {
                yield return Regex.Unescape(source);
            }
        }

        foreach (Match match in markup.Matches(File.ReadAllText(Path.Combine(root, "ClashSharp.Installer", "MainWindow.xaml"))))
        {
            yield return match.Groups[1].Value;
        }
    }

    private static string FindSourceRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClashSharp.slnx"))) { return directory.FullName; }
        }

        throw new DirectoryNotFoundException("The solution directory was not found above the test output.");
    }

    private sealed class InspectionBackend(InstallerRuntimeInspection inspection) : IInstallerRuntimeBackend
    {
        internal InstallerExecutionResult Result { get; init; } =
            new(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false);

        public Task<InstallerRuntimeInspection> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(inspection);

        public Task<InstallerExecutionResult> ExecuteAsync(
            InstallerOperation operation,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => Task.FromResult(Result);

        public void Dispose()
        {
        }
    }
}
