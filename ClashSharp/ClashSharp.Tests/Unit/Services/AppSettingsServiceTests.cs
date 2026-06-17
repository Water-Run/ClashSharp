/*
 * App Settings Service Tests
 * Verifies default user-facing settings
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/AppSettingsServiceTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests defaults exposed by application settings.</summary>
public sealed class AppSettingsServiceTests
{
    /// <summary>Verifies the default mixed proxy port avoids common proxy/VPN defaults.</summary>
    [Fact]
    public void MixedPort_DefaultsTo10000()
    {
        Assert.Equal(10000, AppSettingsService.Instance.MixedPort);
    }
}
