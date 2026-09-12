using System.Collections.ObjectModel;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Groups the settings session and runtime adapters resolved from one generation-owned service container.</summary>
/// <remarks>The containing service lifetime owns disposal; participant constructors must not read storage or start effects.</remarks>
public sealed class SettingsGenerationContext
{
    /// <summary>Creates an immutable generation-local participant catalog.</summary>
    /// <param name="session">Settings authority session owned by the same service container.</param>
    /// <param name="participants">At most one runtime adapter for each declared application kind.</param>
    public SettingsGenerationContext(SettingsAuthoritySession session, IEnumerable<ISettingsApplicationParticipant> participants)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(participants);
        Dictionary<SettingApplicationKind, ISettingsApplicationParticipant> catalog = [];
        foreach (ISettingsApplicationParticipant participant in participants)
        {
            if (participant is null || !Enum.IsDefined(participant.ApplicationKind)
                || !catalog.TryAdd(participant.ApplicationKind, participant))
            {
                throw new ArgumentException("Settings participants must be non-null and unique by declared application kind.", nameof(participants));
            }
        }

        Participants = new ReadOnlyDictionary<SettingApplicationKind, ISettingsApplicationParticipant>(catalog);
    }

    /// <summary>Gets the generation's sole settings session.</summary>
    public SettingsAuthoritySession Session { get; }

    /// <summary>Gets the immutable catalog used for generation-local runtime application.</summary>
    public IReadOnlyDictionary<SettingApplicationKind, ISettingsApplicationParticipant> Participants { get; }
}
