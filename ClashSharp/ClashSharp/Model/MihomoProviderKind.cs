/*
 * Mihomo Provider Kind Model
 * Identifies runtime provider namespaces exposed by mihomo
 *
 * @author: WaterRun
 * @file: Model/MihomoProviderKind.cs
 * @date: 2026-06-24
 */

namespace ClashSharp.Model;

/// <summary>Identifies provider namespaces exposed by mihomo external-controller.</summary>
public enum MihomoProviderKind
{
    /// <summary>Proxy provider namespace.</summary>
    Proxy,

    /// <summary>Rule provider namespace.</summary>
    Rule,
}
