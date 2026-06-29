/*
 * Master Hero Status Layout Service
 * Normalizes and persists the compact status slots shown on the master-control hero card
 *
 * @author: WaterRun
 * @file: Service/MasterHeroStatusLayoutService.cs
 * @date: 2026-06-29
 */

using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.Model;

namespace ClashSharp.Service;

internal interface IMasterHeroStatusLayoutSettings
{
    string MasterHeroStatusLayout { get; set; }
}

internal interface IMasterHeroStatusLayoutService
{
    IReadOnlyList<MasterHeroStatusItemKind> GetLayout();

    IReadOnlyList<MasterHeroStatusItemKind> GetDefaultLayout();

    IReadOnlyList<MasterHeroStatusItemKind> GetCandidates();

    IReadOnlyList<MasterHeroStatusItemKind> SaveLayout(IEnumerable<MasterHeroStatusItemKind> layout);

    IReadOnlyList<MasterHeroStatusItemKind> ResetLayout();
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

    public static MasterHeroStatusLayoutService Instance { get; } = new(AppSettingsService.Instance);

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

    public IReadOnlyList<MasterHeroStatusItemKind> SaveLayout(IEnumerable<MasterHeroStatusItemKind> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        IReadOnlyList<MasterHeroStatusItemKind> normalized = Normalize(layout);
        _settings.MasterHeroStatusLayout = Serialize(normalized);
        return normalized;
    }

    public IReadOnlyList<MasterHeroStatusItemKind> SaveSerializedLayout(string value)
    {
        return SaveLayout(Parse(value));
    }

    public IReadOnlyList<MasterHeroStatusItemKind> ResetLayout()
    {
        _settings.MasterHeroStatusLayout = Serialize(DefaultLayout);
        return DefaultLayout;
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
