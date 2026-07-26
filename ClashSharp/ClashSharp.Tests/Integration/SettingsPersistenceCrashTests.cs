using System.Diagnostics;
using System.Globalization;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies settings authority across real process-termination cuts.</summary>
public sealed class SettingsPersistenceCrashTests
{
    private const int CrashExitCode = 87;
    private const int RepetitionsPerCut = 3;
    private static readonly Guid TransactionId =
        new("30000000-0000-0000-0000-000000000001");

    /// <summary>Verifies initial primary creation is atomic on both sides of its move cut.</summary>
    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion, 0)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion, 1)]
    public async Task InitialSaveCrash_RestartRetainsEmptyOrCompleteInitialEnvelope(
        SettingsPersistenceFaultPoint faultPoint,
        long expectedRevision)
    {
        for (int iteration = 0; iteration < RepetitionsPerCut; iteration++)
        {
            await using DataGenerationTestDirectory directory = new();
            DataGenerationDescriptor descriptor = directory.CreateGeneration(1);

            ProbeResult probe = await RunProbeAsync(
                "initialize",
                descriptor,
                faultPoint);

            AssertCrash(probe);
            JsonSettingsRepository recoveredRepository =
                CreateRepository(descriptor);
            SettingsPersistenceResult recovered =
                await recoveredRepository.OpenAsync(CancellationToken.None);
            Assert.True(recovered.IsSucceeded, recovered.Diagnostic?.Code);
            if (expectedRevision == 0)
            {
                Assert.Null(recovered.Envelope);
            }
            else
            {
                AssertEnvelopeEqual(
                    CreateProbeInitialEnvelope(),
                    recovered.Envelope!);
            }

            AssertCandidatesClean(recoveredRepository);
        }
    }

    /// <summary>
    /// Verifies first backup creation and primary replacement retain complete authority.
    /// </summary>
    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeBackupPromotion, 1)]
    [InlineData(SettingsPersistenceFaultPoint.AfterBackupPromotion, 1)]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion, 1)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion, 2)]
    public async Task UpdateCrash_NewBackupRetainsOneCompleteEnvelope(
        SettingsPersistenceFaultPoint faultPoint,
        long expectedRevision)
    {
        for (int iteration = 0; iteration < RepetitionsPerCut; iteration++)
        {
            await using DataGenerationTestDirectory directory = new();
            DataGenerationDescriptor descriptor = directory.CreateGeneration(1);
            JsonSettingsRepository repository = CreateRepository(descriptor);
            SettingsEnvelope baseline =
                SettingsEnvelopeTestData.CreateMatchingEnvelope();
            SettingsPersistenceResult initialized = await repository.SaveAsync(
                baseline,
                expectedRevision: 0,
                CancellationToken.None);
            Assert.True(
                initialized.IsSucceeded,
                initialized.Diagnostic?.Code);
            SettingsEnvelope target = CreateDarkEnvelope(baseline);

            ProbeResult probe = await RunProbeAsync(
                "update",
                descriptor,
                faultPoint,
                "Dark");

            AssertCrash(probe);
            JsonSettingsRepository recoveredRepository =
                CreateRepository(descriptor);
            SettingsPersistenceResult recovered =
                await recoveredRepository.OpenAsync(CancellationToken.None);
            Assert.True(recovered.IsSucceeded, recovered.Diagnostic?.Code);
            Assert.Equal(expectedRevision, recovered.Envelope!.EnvelopeRevision);
            SettingsEnvelope expected =
                expectedRevision == baseline.EnvelopeRevision
                    ? baseline
                    : target;
            AssertEnvelopeEqual(expected, recovered.Envelope);
            AssertCandidatesClean(recoveredRepository);
        }
    }

    /// <summary>Verifies an existing backup is atomically replaced before the next primary.</summary>
    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeBackupPromotion, 2, 1)]
    [InlineData(SettingsPersistenceFaultPoint.AfterBackupPromotion, 2, 2)]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion, 2, 2)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion, 3, 2)]
    public async Task UpdateCrash_ExistingBackupRetainsExpectedCompleteVersions(
        SettingsPersistenceFaultPoint faultPoint,
        long expectedPrimaryRevision,
        long expectedBackupRevision)
    {
        for (int iteration = 0; iteration < RepetitionsPerCut; iteration++)
        {
            await using DataGenerationTestDirectory directory = new();
            DataGenerationDescriptor descriptor = directory.CreateGeneration(1);
            JsonSettingsRepository repository = CreateRepository(descriptor);
            SettingsEnvelope baseline =
                SettingsEnvelopeTestData.CreateMatchingEnvelope();
            SettingsEnvelope dark =
                SettingsEnvelopeTestData.CreatePendingEnvelope(
                    [("AppThemeMode", "Dark")]);
            Assert.True((await repository.SaveAsync(
                baseline,
                expectedRevision: 0,
                CancellationToken.None)).IsSucceeded);
            Assert.True((await repository.SaveAsync(
                dark,
                expectedRevision: 1,
                CancellationToken.None)).IsSucceeded);
            SettingsEnvelope light = CreateThemeEnvelope(dark, "Light");

            ProbeResult probe = await RunProbeAsync(
                "update",
                descriptor,
                faultPoint,
                "Light");

            AssertCrash(probe);
            SettingsEnvelope backup = SettingsEnvelopeCodec.Decode(
                await File.ReadAllBytesAsync(repository.BackupPath),
                SettingsRegistry.Default);
            Assert.Equal(expectedBackupRevision, backup.EnvelopeRevision);
            AssertEnvelopeEqual(
                expectedBackupRevision == 1 ? baseline : dark,
                backup);
            SettingsPersistenceResult recovered =
                await CreateRepository(descriptor).OpenAsync(
                    CancellationToken.None);
            Assert.True(recovered.IsSucceeded, recovered.Diagnostic?.Code);
            Assert.Equal(
                expectedPrimaryRevision,
                recovered.Envelope!.EnvelopeRevision);
            AssertEnvelopeEqual(
                expectedPrimaryRevision == 2 ? dark : light,
                recovered.Envelope);
            AssertCandidatesClean(repository);
        }
    }

    /// <summary>Verifies backup restoration survives termination around its promotion.</summary>
    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task RecoveryCrash_RestartRestoresTheVerifiedBackup(
        SettingsPersistenceFaultPoint faultPoint)
    {
        for (int iteration = 0; iteration < RepetitionsPerCut; iteration++)
        {
            await using DataGenerationTestDirectory directory = new();
            DataGenerationDescriptor descriptor = directory.CreateGeneration(1);
            JsonSettingsRepository repository = CreateRepository(descriptor);
            SettingsEnvelope baseline =
                SettingsEnvelopeTestData.CreateMatchingEnvelope();
            SettingsEnvelope dark =
                SettingsEnvelopeTestData.CreatePendingEnvelope(
                    [("AppThemeMode", "Dark")]);
            Assert.True((await repository.SaveAsync(
                baseline,
                expectedRevision: 0,
                CancellationToken.None)).IsSucceeded);
            Assert.True((await repository.SaveAsync(
                dark,
                expectedRevision: 1,
                CancellationToken.None)).IsSucceeded);
            await File.WriteAllTextAsync(repository.PrimaryPath, "{broken");

            ProbeResult probe = await RunProbeAsync(
                "recover",
                descriptor,
                faultPoint);

            AssertCrash(probe);
            SettingsPersistenceResult recovered =
                await CreateRepository(descriptor).OpenAsync(
                    CancellationToken.None);
            Assert.True(recovered.IsSucceeded, recovered.Diagnostic?.Code);
            AssertEnvelopeEqual(baseline, recovered.Envelope!);
            Assert.NotEmpty(Directory.EnumerateFiles(
                repository.SettingsDirectoryPath,
                "*.corrupt.*",
                SearchOption.TopDirectoryOnly));
            AssertCandidatesClean(repository);
        }
    }

    private static SettingsEnvelope CreateDarkEnvelope(
        SettingsEnvelope source) =>
        CreateThemeEnvelope(source, "Dark");

    private static SettingsEnvelope CreateThemeEnvelope(
        SettingsEnvelope source,
        string value)
    {
        SettingDefinition definition =
            SettingsRegistry.Default.Get("AppThemeMode");
        SettingNormalizationResult normalized = definition.Normalize(value);
        Assert.True(normalized.IsSuccess, normalized.Error?.Code);
        SettingsEnvelopeEditResult edit =
            new SettingsEnvelopeEditor(SettingsRegistry.Default).ApplyChanges(
                source,
                [new SettingValueChange(definition.Key, normalized.Value!)],
                TransactionId);
        Assert.Equal(SettingsEnvelopeEditOutcome.Updated, edit.Outcome);
        return edit.Envelope;
    }

    private static SettingsEnvelope CreateProbeInitialEnvelope()
    {
        Dictionary<SettingKey, SettingDesiredEntry> desired = [];
        Dictionary<SettingKey, SettingAppliedState> applied = [];
        foreach (SettingDefinition definition in SettingsRegistry.Default.Definitions)
        {
            desired.Add(
                definition.Key,
                new SettingDesiredEntry(
                    definition.DefaultValue,
                    keyDesiredRevision: 1));
            applied.Add(
                definition.Key,
                SettingAppliedState.Verified(
                    definition.DefaultValue,
                    SettingAppliedValueSource.DefaultInitialization,
                    SettingsApplicationBatchEntry.ComputeValueHash(
                        definition.DefaultValue),
                    DateTimeOffset.UnixEpoch));
        }

        return new SettingsEnvelope(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 1,
            desired,
            applied,
            pendingApplications: [],
            migrationHistory: []);
    }

    private static void AssertEnvelopeEqual(
        SettingsEnvelope expected,
        SettingsEnvelope actual)
    {
        Assert.Equal(
            SettingsEnvelopeCodec
                .Encode(expected, SettingsRegistry.Default)
                .ContentHash,
            SettingsEnvelopeCodec
                .Encode(actual, SettingsRegistry.Default)
                .ContentHash);
    }

    private static void AssertCandidatesClean(
        JsonSettingsRepository repository)
    {
        Assert.Empty(Directory.EnumerateFiles(
            repository.SettingsDirectoryPath,
            "*.candidate.*",
            SearchOption.TopDirectoryOnly));
    }

    private static void AssertCrash(ProbeResult probe)
    {
        Assert.Equal(CrashExitCode, probe.ExitCode);
        Assert.True(probe.HasExited);
    }

    private static JsonSettingsRepository CreateRepository(
        DataGenerationDescriptor descriptor) =>
        new(descriptor, SettingsRegistry.Default);

    private static async Task<ProbeResult> RunProbeAsync(
        string operation,
        DataGenerationDescriptor descriptor,
        SettingsPersistenceFaultPoint faultPoint,
        string? targetValue = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(FindProbePath());
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(descriptor.RootPath);
        startInfo.ArgumentList.Add(descriptor.GenerationId.ToString("N"));
        startInfo.ArgumentList.Add(
            descriptor.GenerationNumber.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(faultPoint.ToString());
        if (targetValue is not null)
        {
            startInfo.ArgumentList.Add(targetValue);
        }
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The settings persistence probe could not start.");
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The settings persistence probe did not terminate.");
        }

        string output =
            await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        string error =
            await process.StandardError.ReadToEndAsync(CancellationToken.None);
        Assert.True(
            process.ExitCode == CrashExitCode,
            $"Probe exit {process.ExitCode}. stdout: {output} stderr: {error}");
        return new ProbeResult(process.ExitCode, process.HasExited);
    }

    private static string FindProbePath()
    {
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string path = Path.Combine(
            FindRepositoryRoot(),
            "ClashSharp",
            "ClashSharp.SettingsProbe",
            "bin",
            "x64",
            configuration,
            "net10.0-windows10.0.22000.0",
            "ClashSharp.SettingsProbe.dll");
        Assert.True(
            File.Exists(path),
            $"Settings persistence probe was not built: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "ClashSharp",
                    "ClashSharp.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root from test output.");
    }

    private sealed record ProbeResult(int ExitCode, bool HasExited);
}
