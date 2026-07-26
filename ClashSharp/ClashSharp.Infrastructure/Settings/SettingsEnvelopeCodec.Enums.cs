using System.Text.Json;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Settings;

internal static partial class SettingsEnvelopeCodec
{
    private static string WriteAppliedSource(SettingAppliedValueSource value) =>
        value switch
        {
            SettingAppliedValueSource.DefaultInitialization =>
                "defaultInitialization",
            SettingAppliedValueSource.LegacyMigration => "legacyMigration",
            SettingAppliedValueSource.RuntimeProbe => "runtimeProbe",
            SettingAppliedValueSource.MutationVerification =>
                "mutationVerification",
            SettingAppliedValueSource.StartupReconciliation =>
                "startupReconciliation",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static SettingAppliedValueSource ReadAppliedSource(
        JsonElement element,
        string path) =>
        ReadEnumText(element, path) switch
        {
            "defaultInitialization" =>
                SettingAppliedValueSource.DefaultInitialization,
            "legacyMigration" => SettingAppliedValueSource.LegacyMigration,
            "runtimeProbe" => SettingAppliedValueSource.RuntimeProbe,
            "mutationVerification" =>
                SettingAppliedValueSource.MutationVerification,
            "startupReconciliation" =>
                SettingAppliedValueSource.StartupReconciliation,
            _ => throw EnumError(path),
        };

    private static string WriteUnknownReason(
        SettingAppliedUnknownReason value) =>
        value switch
        {
            SettingAppliedUnknownReason.NotObserved => "notObserved",
            SettingAppliedUnknownReason.ProbeFailed => "probeFailed",
            SettingAppliedUnknownReason.BlockedProbe => "blockedProbe",
            SettingAppliedUnknownReason.InvalidPersistedState =>
                "invalidPersistedState",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static SettingAppliedUnknownReason ReadUnknownReason(
        JsonElement element,
        string path) =>
        ReadEnumText(element, path) switch
        {
            "notObserved" => SettingAppliedUnknownReason.NotObserved,
            "probeFailed" => SettingAppliedUnknownReason.ProbeFailed,
            "blockedProbe" => SettingAppliedUnknownReason.BlockedProbe,
            "invalidPersistedState" =>
                SettingAppliedUnknownReason.InvalidPersistedState,
            _ => throw EnumError(path),
        };

    private static string WriteUnknownHandling(
        SettingAppliedUnknownHandling value) =>
        value switch
        {
            SettingAppliedUnknownHandling.QueueApplication =>
                "queueApplication",
            SettingAppliedUnknownHandling.UseSafeFallback =>
                "useSafeFallback",
            SettingAppliedUnknownHandling.BlockOperation => "blockOperation",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static SettingAppliedUnknownHandling ReadUnknownHandling(
        JsonElement element,
        string path) =>
        ReadEnumText(element, path) switch
        {
            "queueApplication" =>
                SettingAppliedUnknownHandling.QueueApplication,
            "useSafeFallback" =>
                SettingAppliedUnknownHandling.UseSafeFallback,
            "blockOperation" =>
                SettingAppliedUnknownHandling.BlockOperation,
            _ => throw EnumError(path),
        };

    private static string WriteBatchKind(SettingsApplicationBatchKind value) =>
        value switch
        {
            SettingsApplicationBatchKind.LiveReconcile => "liveReconcile",
            SettingsApplicationBatchKind.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static SettingsApplicationBatchKind ReadBatchKind(
        JsonElement element,
        string path) =>
        ReadEnumText(element, path) switch
        {
            "liveReconcile" => SettingsApplicationBatchKind.LiveReconcile,
            "restart" => SettingsApplicationBatchKind.Restart,
            _ => throw EnumError(path),
        };

    private static string WriteBatchState(SettingsApplicationBatchState value) =>
        value switch
        {
            SettingsApplicationBatchState.Pending => "pending",
            SettingsApplicationBatchState.Running => "running",
            SettingsApplicationBatchState.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static SettingsApplicationBatchState ReadBatchState(
        JsonElement element,
        string path) =>
        ReadEnumText(element, path) switch
        {
            "pending" => SettingsApplicationBatchState.Pending,
            "running" => SettingsApplicationBatchState.Running,
            "failed" => SettingsApplicationBatchState.Failed,
            _ => throw EnumError(path),
        };

    private static string WriteApplicationKind(SettingApplicationKind value) =>
        value switch
        {
            SettingApplicationKind.Internal => "internal",
            SettingApplicationKind.Appearance => "appearance",
            SettingApplicationKind.Network => "network",
            SettingApplicationKind.StartupTask => "startupTask",
            SettingApplicationKind.Sampling => "sampling",
            SettingApplicationKind.Triggers => "triggers",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static SettingApplicationKind ReadApplicationKind(
        JsonElement element,
        string path) =>
        ReadEnumText(element, path) switch
        {
            "internal" => SettingApplicationKind.Internal,
            "appearance" => SettingApplicationKind.Appearance,
            "network" => SettingApplicationKind.Network,
            "startupTask" => SettingApplicationKind.StartupTask,
            "sampling" => SettingApplicationKind.Sampling,
            "triggers" => SettingApplicationKind.Triggers,
            _ => throw EnumError(path),
        };

    private static string ReadEnumText(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw EnumError(path);
        }

        return element.GetString() ?? throw EnumError(path);
    }

    private static SettingsEnvelopeCodecException EnumError(string path) =>
        Error("settings.persistence.envelope.enum_invalid", path);
}
