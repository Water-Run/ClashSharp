/*
 * Windows Proxy State Model
 * Represents the current Windows system proxy registry state
 *
 * @author: WaterRun
 * @file: Model/WindowsProxyState.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Represents the Windows per-user system proxy state.</summary>
/// <param name="IsEnabled">True when the Windows proxy switch is enabled.</param>
/// <param name="ProxyServer">Configured proxy server string; empty when no server value is present.</param>
/// <remarks>
/// Invariants: <paramref name="ProxyServer"/> is never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct WindowsProxyState(bool IsEnabled, string ProxyServer);
