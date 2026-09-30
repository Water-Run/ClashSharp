using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Service;

internal interface IMasterHeroStatusLayoutSettings
{
    string MasterHeroStatusLayout { get; }

    Task SaveHeroStatusLayoutAsync(string layout, CancellationToken cancellationToken);
}

internal sealed class MasterHeroStatusLayoutService : IMasterHeroStatusLayoutService
{
    private const int SlotCount = 8;

    private readonly IMasterHeroStatusLayoutSettings _settings;

    public static IReadOnlyList<MasterHeroStatusItemKind> DefaultLayout { get; } =
    [
        MasterHeroStatusItemKind.CoreStatus,
        MasterHeroStatusItemKind.SystemProxy,
        MasterHeroStatusItemKind.TransparentProxy,
        MasterHeroStatusItemKind.CurrentNode,
        MasterHeroStatusItemKind.UploadRate,
        MasterHeroStatusItemKind.DownloadRate,
        MasterHeroStatusItemKind.TotalTraffic,
        MasterHeroStatusItemKind.Availability,
    ];

    public static IReadOnlyList<MasterHeroStatusItemKind> Candidates { get; } =
    [
        MasterHeroStatusItemKind.CoreStatus,
        MasterHeroStatusItemKind.SystemProxy,
        MasterHeroStatusItemKind.TransparentProxy,
        MasterHeroStatusItemKind.CurrentNode,
        MasterHeroStatusItemKind.Latency,
        MasterHeroStatusItemKind.UploadRate,
        MasterHeroStatusItemKind.DownloadRate,
        MasterHeroStatusItemKind.TotalTraffic,
        MasterHeroStatusItemKind.ActiveConnections,
        MasterHeroStatusItemKind.CurrentMode,
        MasterHeroStatusItemKind.ActiveProfile,
        MasterHeroStatusItemKind.MihomoService,
        MasterHeroStatusItemKind.StartupLaunch,
        MasterHeroStatusItemKind.Availability,
    ];

    public MasterHeroStatusLayoutService(IMasterHeroStatusLayoutSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyList<MasterHeroStatusItemKind> GetLayout()
    {
        return Normalize(Parse(_settings.MasterHeroStatusLayout));
    }

    public IReadOnlyList<MasterHeroStatusItemKind> GetDefaultLayout()
    {
        return DefaultLayout;
    }

    public IReadOnlyList<MasterHeroStatusItemKind> GetCandidates()
    {
        return Candidates;
    }

    public async Task<IReadOnlyList<MasterHeroStatusItemKind>> SaveLayoutAsync(
        IEnumerable<MasterHeroStatusItemKind> layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        IReadOnlyList<MasterHeroStatusItemKind> normalized = Normalize(layout);
        cancellationToken.ThrowIfCancellationRequested();
        await _settings.SaveHeroStatusLayoutAsync(Serialize(normalized), cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public Task<IReadOnlyList<MasterHeroStatusItemKind>> SaveSerializedLayoutAsync(string value, CancellationToken cancellationToken)
    {
        return SaveLayoutAsync(Parse(value), cancellationToken);
    }

    public Task<IReadOnlyList<MasterHeroStatusItemKind>> ResetLayoutAsync(CancellationToken cancellationToken)
    {
        return SaveLayoutAsync(DefaultLayout, cancellationToken);
    }

    private static IReadOnlyList<MasterHeroStatusItemKind> Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<MasterHeroStatusItemKind> result = [];
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse(token, ignoreCase: true, out MasterHeroStatusItemKind kind)
                && Candidates.Contains(kind))
            {
                result.Add(kind);
            }
        }

        return result;
    }

    private static IReadOnlyList<MasterHeroStatusItemKind> Normalize(IEnumerable<MasterHeroStatusItemKind> layout)
    {
        List<MasterHeroStatusItemKind> result = [];
        HashSet<MasterHeroStatusItemKind> seen = [];

        foreach (MasterHeroStatusItemKind kind in layout)
        {
            if (Candidates.Contains(kind) && seen.Add(kind))
            {
                result.Add(kind);
            }

            if (result.Count == SlotCount)
            {
                return result;
            }
        }

        foreach (MasterHeroStatusItemKind kind in DefaultLayout.Concat(Candidates))
        {
            if (seen.Add(kind))
            {
                result.Add(kind);
            }

            if (result.Count == SlotCount)
            {
                return result;
            }
        }

        return result;
    }

    private static string Serialize(IEnumerable<MasterHeroStatusItemKind> layout)
    {
        return string.Join(",", layout.Select(static kind => kind.ToString()));
    }
}
