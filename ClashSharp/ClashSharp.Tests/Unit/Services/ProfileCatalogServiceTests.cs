/*
 * Profile Catalog Service Tests
 * Verifies profile catalog state through injected settings and dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/ProfileCatalogServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for profile catalog composition.</summary>
public sealed class ProfileCatalogServiceTests
{
    /// <summary>Verifies a missing catalog creates the localized built-in profile and marks it active from injected settings.</summary>
    [Fact]
    public void GetProfiles_WhenCatalogMissing_ReturnsLocalizedBuiltInProfile()
    {
        using TempFile tempFile = new();
        FakeProfileCatalogSettings settings = new()
        {
            ActiveProfileId = ProfileCatalogIds.BuiltInDirect,
        };
        ProfileCatalogService service = CreateService(tempFile.Path, settings);

        ConfigurationProfile profile = Assert.Single(service.GetProfiles());

        Assert.Equal(ProfileCatalogIds.BuiltInDirect, profile.Id);
        Assert.Equal("localized direct", profile.Name);
        Assert.Equal("localized available", profile.Status);
        Assert.True(profile.IsActive);
    }

    /// <summary>Verifies activating an existing profile writes through the injected settings store.</summary>
    [Fact]
    public void TrySetActiveProfile_WhenProfileExists_UpdatesInjectedSettings()
    {
        using TempFile tempFile = new();
        FakeProfileCatalogSettings settings = new();
        ProfileCatalogService service = CreateService(tempFile.Path, settings);

        bool updated = service.TrySetActiveProfile(ProfileCatalogIds.BuiltInDirect);

        Assert.True(updated);
        Assert.Equal(ProfileCatalogIds.BuiltInDirect, settings.ActiveProfileId);
    }

    private static ProfileCatalogService CreateService(string catalogPath, FakeProfileCatalogSettings settings)
    {
        return new ProfileCatalogService(
            catalogPath,
            settings,
            new FakeProfileCatalogCoreConfiguration(),
            new FakeProfileCatalogLog(),
            key => key switch
            {
                "ProfileCatalog.BuiltInDirect.Name" => "localized direct",
                "ProfileCatalog.Status.Available" => "localized available",
                _ => key,
            });
    }

    private sealed class FakeProfileCatalogSettings : IProfileCatalogSettings
    {
        public string ActiveProfileId { get; set; } = string.Empty;
    }

    private sealed class FakeProfileCatalogCoreConfiguration : IProfileCatalogCoreConfiguration
    {
        public Task<ProfileImportResult> ImportProfileConfigurationAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public CoreConfigurationState EnsureDefaultConfiguration()
        {
            throw new NotSupportedException();
        }

        public Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeProfileCatalogLog : IProfileCatalogLog
    {
        public void AppendLog(string level, string category, string message, string? detail)
        {
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clashsharp-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "ProfileCatalog.json");
        }

        public string Path { get; }

        public void Dispose()
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
