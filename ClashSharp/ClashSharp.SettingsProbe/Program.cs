using System.Globalization;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;

return await SettingsProbeProgram.RunAsync(args);

internal static class SettingsProbeProgram
{
    private const int CrashExitCode = 87;
    private static readonly Guid TransactionId =
        new("30000000-0000-0000-0000-000000000001");

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count is < 5 or > 6
            || !Guid.TryParseExact(args[2], "N", out Guid generationId)
            || !long.TryParse(
                args[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long generationNumber)
            || !Enum.TryParse(
                args[4],
                ignoreCase: false,
                out SettingsPersistenceFaultPoint faultPoint)
            || !Enum.IsDefined(faultPoint))
        {
            return 64;
        }

        DataGenerationDescriptor descriptor = new(
            generationId,
            generationNumber,
            Path.GetFullPath(args[1]));
        JsonSettingsRepository repository = new(
            descriptor,
            SettingsRegistry.Default,
            new TerminatingFaultInjector(faultPoint));
        return args[0] switch
        {
            "initialize" when args.Count == 5 =>
                await InitializeAsync(repository),
            "update" when args.Count == 6 =>
                await UpdateAsync(repository, args[5]),
            "recover" when args.Count == 5 =>
                await RecoverAsync(repository),
            _ => 64,
        };
    }

    private static async Task<int> InitializeAsync(
        JsonSettingsRepository repository)
    {
        SettingsPersistenceResult saved = await repository.SaveAsync(
            CreateInitialEnvelope(),
            expectedRevision: 0,
            CancellationToken.None);
        return saved.IsSucceeded ? 0 : 1;
    }

    private static async Task<int> UpdateAsync(
        JsonSettingsRepository repository,
        string targetValue)
    {
        SettingsPersistenceResult opened =
            await repository.OpenAsync(CancellationToken.None);
        if (!opened.IsSucceeded || opened.Envelope is null)
        {
            return 1;
        }

        SettingDefinition theme =
            SettingsRegistry.Default.Get("AppThemeMode");
        SettingNormalizationResult normalized = theme.Normalize(targetValue);
        if (!normalized.IsSuccess)
        {
            return 2;
        }

        SettingsEnvelopeEditResult edit =
            new SettingsEnvelopeEditor(SettingsRegistry.Default).ApplyChanges(
                opened.Envelope,
                [new SettingValueChange(theme.Key, normalized.Value!)],
                TransactionId);
        if (edit.Outcome != SettingsEnvelopeEditOutcome.Updated)
        {
            return 3;
        }

        SettingsPersistenceResult saved = await repository.SaveAsync(
            edit.Envelope,
            opened.Envelope.EnvelopeRevision,
            CancellationToken.None);
        return saved.IsSucceeded ? 0 : 4;
    }

    private static async Task<int> RecoverAsync(
        JsonSettingsRepository repository)
    {
        SettingsPersistenceResult opened =
            await repository.OpenAsync(CancellationToken.None);
        return opened.IsSucceeded && opened.Envelope is not null ? 0 : 1;
    }

    private static SettingsEnvelope CreateInitialEnvelope()
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

    private sealed class TerminatingFaultInjector(
        SettingsPersistenceFaultPoint selectedFaultPoint)
        : ISettingsPersistenceFaultInjector
    {
        public Task InjectAsync(
            SettingsPersistenceFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint == selectedFaultPoint)
            {
                Environment.Exit(CrashExitCode);
            }

            return Task.CompletedTask;
        }
    }
}
