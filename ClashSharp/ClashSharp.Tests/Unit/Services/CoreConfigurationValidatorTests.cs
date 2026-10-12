using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Exercises bundled asset preparation through the production native validation adapter.</summary>
public sealed class CoreConfigurationValidatorTests
{
    [Fact]
    public async Task ValidateAsync_MakesBundledGeoDataAvailableOnlyForNativeValidation()
    {
        using Fixture fixture = new();
        CoreConfigurationValidator validator = new(FindProbeExecutable(), fixture.Assets);

        await validator.ValidateAsync(fixture.Working, fixture.Configuration, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(fixture.Working, "validation.pid")));
        foreach (string name in Fixture.Names)
        {
            Assert.False(File.Exists(Path.Combine(fixture.Working, name)));
            Assert.Equal(Fixture.AssetBytes(name), await File.ReadAllBytesAsync(Path.Combine(fixture.Assets, name)));
        }
        Assert.Equal("proxies: []\nproxy-groups: []\nrules: ['MATCH,DIRECT']\n", await File.ReadAllTextAsync(fixture.Configuration));
    }

    [Fact]
    public async Task ValidateAsync_PreservesExistingVerifiedAssets()
    {
        using Fixture fixture = new();
        string existing = Path.Combine(fixture.Working, "Country.mmdb");
        File.Copy(Path.Combine(fixture.Assets, "Country.mmdb"), existing);

        await new CoreConfigurationValidator(FindProbeExecutable(), fixture.Assets)
            .ValidateAsync(fixture.Working, fixture.Configuration, CancellationToken.None);

        Assert.Equal(Fixture.AssetBytes("Country.mmdb"), await File.ReadAllBytesAsync(existing));
        Assert.False(File.Exists(Path.Combine(fixture.Working, "GeoSite.dat")));
    }

    [Fact]
    public async Task ValidateAsync_NativeRejectionRemovesOnlyTemporaryAssets()
    {
        using Fixture fixture = new();
        await File.AppendAllTextAsync(fixture.Configuration, "# process-probe: validation-failure\n");
        string before = await File.ReadAllTextAsync(fixture.Configuration);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreConfigurationValidator(FindProbeExecutable(), fixture.Assets)
            .ValidateAsync(fixture.Working, fixture.Configuration, CancellationToken.None));

        AssertNoTemporaryAssets(fixture);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.Configuration));
    }

    [Fact]
    public async Task ValidateAsync_CancellationWaitsForNativeExitBeforeRemovingAssets()
    {
        using Fixture fixture = new();
        await File.AppendAllTextAsync(fixture.Configuration, "# process-probe: validation-hang\n");
        using CancellationTokenSource cancellation = new();
        Task validation = new CoreConfigurationValidator(FindProbeExecutable(), fixture.Assets)
            .ValidateAsync(fixture.Working, fixture.Configuration, cancellation.Token);
        string marker = Path.Combine(fixture.Working, "validation.pid");
        Stopwatch watch = Stopwatch.StartNew();
        while (!File.Exists(marker) && !validation.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }
        Assert.True(File.Exists(marker), "The native validator must reach its running state.");
        using Process native = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(native.HasExited);
        AssertNoTemporaryAssets(fixture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcquireAsync_RejectsChangedAssetsAndRollsBackEarlierCopies(bool changeExistingProfileAsset)
    {
        using Fixture fixture = new();
        string changed = Path.Combine(changeExistingProfileAsset ? fixture.Working : fixture.Assets, "ASN.mmdb");
        await File.WriteAllTextAsync(changed, "user-content-must-remain");

        await Assert.ThrowsAsync<InvalidDataException>(() => CoreConfigurationGeoDataLease.AcquireAsync(
            fixture.Assets, fixture.Working, CancellationToken.None));

        Assert.Equal("user-content-must-remain", await File.ReadAllTextAsync(changed));
        foreach (string name in Fixture.Names.Where(name => name != "ASN.mmdb"))
        {
            Assert.False(File.Exists(Path.Combine(fixture.Working, name)));
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"files\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"files\":[]}")]
    public async Task AcquireAsync_RejectsInvalidManifestWithoutCreatingAssets(string manifest)
    {
        using Fixture fixture = new();
        await File.WriteAllTextAsync(Path.Combine(fixture.Assets, "manifest.json"), manifest);

        await Assert.ThrowsAsync<InvalidDataException>(() => CoreConfigurationGeoDataLease.AcquireAsync(
            fixture.Assets, fixture.Working, CancellationToken.None));

        AssertNoTemporaryAssets(fixture);
    }

    [Theory]
    [InlineData("../outside.mmdb")]
    [InlineData("GeoSite.dat")]
    public async Task AcquireAsync_RejectsEscapingOrDuplicateNamesBeforeStaging(string invalidName)
    {
        using Fixture fixture = new();
        string path = Path.Combine(fixture.Assets, "manifest.json");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("Country.mmdb", invalidName, StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(() => CoreConfigurationGeoDataLease.AcquireAsync(
            fixture.Assets, fixture.Working, CancellationToken.None));

        AssertNoTemporaryAssets(fixture);
    }

    [Fact]
    public async Task AcquireAsync_PreservesAConflictingDirectoryAndItsContents()
    {
        using Fixture fixture = new();
        string directory = Directory.CreateDirectory(Path.Combine(fixture.Working, "ASN.mmdb")).FullName;
        string userFile = Path.Combine(directory, "preserve.txt");
        await File.WriteAllTextAsync(userFile, "preserved");

        Exception? failure = await Record.ExceptionAsync(() => CoreConfigurationGeoDataLease.AcquireAsync(
            fixture.Assets, fixture.Working, CancellationToken.None));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("preserved", await File.ReadAllTextAsync(userFile));
        Assert.False(File.Exists(Path.Combine(fixture.Working, "Country.mmdb")));
    }

    [Fact]
    public async Task AcquireAsync_SerializesCanonicalDirectoryUntilCleanupCompletes()
    {
        using Fixture fixture = new();
        using CoreConfigurationGeoDataLease first = await CoreConfigurationGeoDataLease.AcquireAsync(fixture.Assets, fixture.Working, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        Task<CoreConfigurationGeoDataLease> waiting = CoreConfigurationGeoDataLease.AcquireAsync(
            fixture.Assets, fixture.Working + Path.DirectorySeparatorChar, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(File.Exists(Path.Combine(fixture.Working, "GeoSite.dat")));
        first.Dispose();
        AssertNoTemporaryAssets(fixture);
        using CoreConfigurationGeoDataLease next = await CoreConfigurationGeoDataLease.AcquireAsync(fixture.Assets, fixture.Working, CancellationToken.None);
    }

    private static void AssertNoTemporaryAssets(Fixture fixture)
    {
        foreach (string name in Fixture.Names)
        {
            Assert.False(File.Exists(Path.Combine(fixture.Working, name)));
        }
    }

    private static string FindProbeExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        List<string> parts = [directory.FullName, "ClashSharp", "ClashSharp.ProcessProbe", "bin"];
        if (AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar).Contains("x64", StringComparer.OrdinalIgnoreCase))
        {
            parts.Add("x64");
        }
#if DEBUG
        parts.Add("Debug");
#else
        parts.Add("Release");
#endif
        parts.AddRange(["net10.0", "ClashSharp.ProcessProbe.exe"]);
        string path = Path.Combine([.. parts]);
        Assert.True(File.Exists(path), "The native process probe must be built.");
        return path;
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly string[] Names = ["Country.mmdb", "GeoIP.dat", "GeoSite.dat", "ASN.mmdb"];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "clashsharp-validation-assets-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Assets = Directory.CreateDirectory(Path.Combine(_root, "bundled")).FullName;
            Working = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
            Configuration = Path.Combine(Working, "config.yaml");
            File.WriteAllText(Configuration, "proxies: []\nproxy-groups: []\nrules: ['MATCH,DIRECT']\n");
            foreach (string name in Names)
            {
                File.WriteAllBytes(Path.Combine(Assets, name), AssetBytes(name));
            }
            File.WriteAllText(Path.Combine(Assets, "manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                files = Names.Select(name => new
                {
                    name,
                    length = AssetBytes(name).Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(AssetBytes(name))),
                }),
            }));
        }

        internal string Assets { get; }
        internal string Working { get; }
        internal string Configuration { get; }
        internal static byte[] AssetBytes(string name) => Encoding.UTF8.GetBytes("owned-test-asset:" + name);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
