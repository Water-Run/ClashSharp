using System.Collections.Concurrent;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for local mihomo configuration management.</summary>
public sealed class CoreConfigurationServiceTests
{
    /// <summary>Verifies runtime configuration generation uses injected settings and scoped paths.</summary>
    [Fact]
    public void EnsureConfiguration_UsesInjectedSettingsAndWritesRuntimeConfiguration()
    {
        using TempDirectory tempDirectory = new();
        FakeCoreConfigurationSettings settings = new()
        {
            ActiveProfileId = ProfileCatalogIds.BuiltInDirect,
            MixedPort = 19090,
            TransparentProxyEnabled = true,
        };
        CoreConfigurationService service = CreateService(tempDirectory.Path, settings);

        CoreConfigurationState state = service.EnsureConfiguration(ClashSharpMode.FullTakeover);

        string configurationText = File.ReadAllText(state.ConfigPath);
        Assert.True(state.Exists);
        Assert.Equal(Path.Combine(tempDirectory.Path, "config.yaml"), state.ConfigPath);
        Assert.Contains("mixed-port: 19090", configurationText, StringComparison.Ordinal);
        Assert.Contains("mode: global", configurationText, StringComparison.Ordinal);
        Assert.Contains("tun:\n", configurationText, StringComparison.Ordinal);
    }

    /// <summary>Verifies profile import uses injected metrics, validation, and localization dependencies.</summary>
    [Fact]
    public async Task ImportProfileConfigurationAsync_UsesInjectedMetricsValidatorAndLocalization()
    {
        using TempDirectory tempDirectory = new();
        FakeCoreConfigurationProfileMetrics metrics = new()
        {
            NodeCount = 3,
            RuleCount = 4,
        };
        FakeCoreConfigurationValidator validator = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, metrics: metrics, validator: validator);

        ProfileImportResult result = await service.ImportProfileConfigurationAsync(
            "profile:one",
            " Test Profile ",
            """
            proxies:
              - name: DIRECT
                type: direct
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            rules:
              - MATCH,DIRECT
            """,
            CancellationToken.None);

        Assert.Equal("profile-one", result.ProfileId);
        Assert.Equal("Test Profile", result.ProfileName);
        Assert.Equal(3, result.NodeCount);
        Assert.Equal(4, result.RuleCount);
        Assert.Equal("imported", result.Message);
        Assert.True(File.Exists(result.ConfigPath));
        CoreValidationRequest request = Assert.Single(validator.Requests);
        Assert.Equal(Path.GetDirectoryName(result.ConfigPath), request.WorkingDirectory);
        Assert.StartsWith(
            Path.Combine(request.WorkingDirectory, "config.yaml.staging."),
            request.ConfigurationPath,
            StringComparison.Ordinal);
        Assert.NotEqual(result.ConfigPath, request.ConfigurationPath);
        Assert.False(File.Exists(request.ConfigurationPath));
        AssertNoImportSidecars(Path.GetDirectoryName(result.ConfigPath)!);
    }

    [Fact]
    public async Task TryReadProfileConfigurationText_SerializesWithProfileOverwrite()
    {
        using TempDirectory tempDirectory = new();
        const string profileId = "profile-one";
        const string oldConfiguration =
            "proxies:\n"
            + "  - name: old-node\n"
            + "    type: direct\n"
            + "rules:\n"
            + "  - MATCH,DIRECT\n";
        const string newConfiguration =
            "proxies:\n"
            + "  - name: new-node-one\n"
            + "    type: direct\n"
            + "  - name: new-node-two\n"
            + "    type: direct\n"
            + "proxy-groups:\n"
            + "  - name: GLOBAL\n"
            + "    type: select\n"
            + "    proxies:\n"
            + "      - new-node-one\n"
            + "      - new-node-two\n"
            + "rules:\n"
            + "  - DOMAIN,new.example,DIRECT\n"
            + "  - MATCH,DIRECT\n";
        string profileDirectory = Path.Combine(tempDirectory.Path, "profiles", profileId);
        string profilePath = Path.Combine(profileDirectory, "config.yaml");
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(profilePath, oldConfiguration);
        using BlockingProfileTextIo textIo = new();
        CoordinatedProfileMetrics metrics = new();
        CoreConfigurationService service = new(
            tempDirectory.Path,
            new FakeCoreConfigurationSettings(),
            metrics,
            new FakeCoreConfigurationValidator(),
            static key => key,
            textIo.ReadAllText,
            textIo.WriteAllText);

        Task<(bool Found, string? Text)> readTask = Task.Run(() =>
        {
            bool found = service.TryReadProfileConfigurationText(profileId, out string? text);
            return (found, text);
        });
        Assert.True(textIo.ReadEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<ProfileImportResult> overwriteTask = Task.Run(
            () => service.ImportProfileConfigurationAsync(
                profileId,
                "Profile One",
                newConfiguration,
                CancellationToken.None));
        Assert.True(metrics.CountingCompleted.Wait(TimeSpan.FromSeconds(5)));
        bool writeOverlappedRead = textIo.WriteEntered.Wait(TimeSpan.FromMilliseconds(250));

        textIo.AllowRead.Set();
        (bool found, string? readText) = await readTask;
        await overwriteTask;

        Assert.False(writeOverlappedRead);
        Assert.True(found);
        Assert.Equal(oldConfiguration, readText);
        Assert.True(service.TryReadProfileConfigurationText(profileId, out string? finalText));
        Assert.Equal(newConfiguration, finalText);
    }

    [Fact]
    public async Task ImportProfileConfigurationAsync_FailedEarlierTransactionCannotRollbackLaterSuccess()
    {
        using TempDirectory tempDirectory = new();
        const string profileId = "same-profile";
        WriteCommittedProfile(tempDirectory.Path, profileId, ProfileConfiguration("old-node"));
        CoordinatedImportValidator validator = new();
        ValidationStep failed = validator.AddBlockedFailure(
            "failed-node",
            new InvalidOperationException("rejected"));
        validator.AddImmediateSuccess("successful-node");
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator: validator);

        Task<ProfileImportResult> failedImport = service.ImportProfileConfigurationAsync(
            profileId,
            "Failed",
            ProfileConfiguration("failed-node"),
            CancellationToken.None);
        await failed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<ProfileImportResult> successfulImport = service.ImportProfileConfigurationAsync(
            profileId,
            "Successful",
            ProfileConfiguration("successful-node"),
            CancellationToken.None);
        Assert.False(successfulImport.IsCompleted);

        failed.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedImport);
        ProfileImportResult result = await successfulImport;

        Assert.Equal(ProfileConfiguration("successful-node"), File.ReadAllText(result.ConfigPath));
        Assert.Equal(2, validator.Requests.Count);
        Assert.Equal(2, validator.Requests.Select(static request => request.ConfigurationPath).Distinct().Count());
        AssertNoImportSidecars(Path.GetDirectoryName(result.ConfigPath)!);
    }

    [Fact]
    public async Task ImportProfileConfigurationAsync_TwoSuccessfulTransactionsCommitInGateOrder()
    {
        using TempDirectory tempDirectory = new();
        const string profileId = "same-profile";
        WriteCommittedProfile(tempDirectory.Path, profileId, ProfileConfiguration("old-node"));
        CoordinatedImportValidator validator = new();
        ValidationStep first = validator.AddBlockedSuccess("first-node");
        validator.AddImmediateSuccess("second-node");
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator: validator);

        Task<ProfileImportResult> firstImport = service.ImportProfileConfigurationAsync(
            profileId,
            "First",
            ProfileConfiguration("first-node"),
            CancellationToken.None);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<ProfileImportResult> secondImport = service.ImportProfileConfigurationAsync(
            profileId,
            "Second",
            ProfileConfiguration("second-node"),
            CancellationToken.None);
        Assert.False(secondImport.IsCompleted);

        first.Release();
        await firstImport;
        ProfileImportResult secondResult = await secondImport;

        Assert.Equal(ProfileConfiguration("second-node"), File.ReadAllText(secondResult.ConfigPath));
        Assert.Equal(2, validator.Requests.Select(static request => request.ConfigurationPath).Distinct().Count());
        AssertNoImportSidecars(Path.GetDirectoryName(secondResult.ConfigPath)!);
    }

    [Fact]
    public async Task ImportProfileConfigurationAsync_CancelledTransactionReleasesGateWithoutTouchingLaterSuccess()
    {
        using TempDirectory tempDirectory = new();
        const string profileId = "same-profile";
        WriteCommittedProfile(tempDirectory.Path, profileId, ProfileConfiguration("old-node"));
        CoordinatedImportValidator validator = new();
        ValidationStep cancelled = validator.AddCancellationWait("cancelled-node");
        validator.AddImmediateSuccess("successful-node");
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator: validator);
        using CancellationTokenSource cancellation = new();

        Task<ProfileImportResult> cancelledImport = service.ImportProfileConfigurationAsync(
            profileId,
            "Cancelled",
            ProfileConfiguration("cancelled-node"),
            cancellation.Token);
        await cancelled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<ProfileImportResult> successfulImport = service.ImportProfileConfigurationAsync(
            profileId,
            "Successful",
            ProfileConfiguration("successful-node"),
            CancellationToken.None);
        Assert.False(successfulImport.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledImport);
        ProfileImportResult result = await successfulImport;

        Assert.Equal(ProfileConfiguration("successful-node"), File.ReadAllText(result.ConfigPath));
        AssertNoImportSidecars(Path.GetDirectoryName(result.ConfigPath)!);
    }

    [Fact]
    public async Task ImportProfileConfigurationAsync_DifferentProfilesValidateConcurrently()
    {
        using TempDirectory tempDirectory = new();
        CoordinatedImportValidator validator = new();
        ValidationStep alpha = validator.AddBlockedSuccess("alpha-node");
        ValidationStep beta = validator.AddBlockedSuccess("beta-node");
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator: validator);

        Task<ProfileImportResult> alphaImport = service.ImportProfileConfigurationAsync(
            "alpha",
            "Alpha",
            ProfileConfiguration("alpha-node"),
            CancellationToken.None);
        await alpha.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<ProfileImportResult> betaImport = service.ImportProfileConfigurationAsync(
            "beta",
            "Beta",
            ProfileConfiguration("beta-node"),
            CancellationToken.None);
        await beta.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        alpha.Release();
        beta.Release();
        ProfileImportResult[] results = await Task.WhenAll(alphaImport, betaImport);

        Assert.Equal(ProfileConfiguration("alpha-node"), File.ReadAllText(results[0].ConfigPath));
        Assert.Equal(ProfileConfiguration("beta-node"), File.ReadAllText(results[1].ConfigPath));
        Assert.NotEqual(
            Path.GetDirectoryName(results[0].ConfigPath),
            Path.GetDirectoryName(results[1].ConfigPath));
        AssertNoImportSidecars(Path.GetDirectoryName(results[0].ConfigPath)!);
        AssertNoImportSidecars(Path.GetDirectoryName(results[1].ConfigPath)!);
    }

    [Fact]
    public async Task ImportProfileConfigurationAsync_ProcessFatalValidationFailurePropagatesAndPreservesCommittedFile()
    {
        using TempDirectory tempDirectory = new();
        const string profileId = "fatal-profile";
        string original = ProfileConfiguration("old-node");
        WriteCommittedProfile(tempDirectory.Path, profileId, original);
        CoordinatedImportValidator validator = new();
        validator.AddImmediateFailure(
            "fatal-node",
            Activator.CreateInstance<OutOfMemoryException>());
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator: validator);

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            service.ImportProfileConfigurationAsync(
                profileId,
                "Fatal",
                ProfileConfiguration("fatal-node"),
                CancellationToken.None));

        string profileDirectory = Path.Combine(tempDirectory.Path, "profiles", profileId);
        Assert.Equal(original, File.ReadAllText(Path.Combine(profileDirectory, "config.yaml")));
        AssertNoImportSidecars(profileDirectory);
    }

    [Fact]
    public async Task ValidateImportedProfileAsync_WaitsForSameProfileImportAndObservesCommittedSnapshot()
    {
        using TempDirectory tempDirectory = new();
        const string profileId = "same-profile";
        WriteCommittedProfile(tempDirectory.Path, profileId, ProfileConfiguration("old-node"));
        ImportThenValidateValidator validator = new();
        MarkerProfileMetrics metrics = new("new-node", nodeCount: 7, ruleCount: 9);
        CoreConfigurationService service = CreateService(
            tempDirectory.Path,
            metrics: metrics,
            validator: validator);

        Task<ProfileImportResult> import = service.ImportProfileConfigurationAsync(
            profileId,
            "Updated",
            ProfileConfiguration("new-node"),
            CancellationToken.None);
        await validator.ImportEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<ProfileImportResult> validation = service.ValidateImportedProfileAsync(
            profileId,
            CancellationToken.None);
        try
        {
            Assert.False(validation.IsCompleted);
        }
        finally
        {
            validator.ReleaseImport();
        }

        ProfileImportResult importResult = await import;
        ProfileImportResult validationResult = await validation;

        Assert.Equal(importResult.ConfigPath, validationResult.ConfigPath);
        Assert.Equal(7, validationResult.NodeCount);
        Assert.Equal(9, validationResult.RuleCount);
        Assert.Equal(ProfileConfiguration("new-node"), validator.ValidatedCommittedText);
        Assert.Equal(ProfileConfiguration("new-node"), File.ReadAllText(importResult.ConfigPath));
        AssertNoImportSidecars(Path.GetDirectoryName(importResult.ConfigPath)!);
    }

    private static CoreConfigurationService CreateService(
        string configurationDirectory,
        FakeCoreConfigurationSettings? settings = null,
        ICoreConfigurationProfileMetrics? metrics = null,
        ICoreConfigurationValidator? validator = null)
    {
        return new CoreConfigurationService(
            configurationDirectory,
            settings ?? new FakeCoreConfigurationSettings(),
            metrics ?? new FakeCoreConfigurationProfileMetrics(),
            validator ?? new FakeCoreConfigurationValidator(),
            key => key switch
            {
                "CoreConfiguration.Imported" => "imported",
                "CoreConfiguration.Validated" => "validated",
                _ => key,
            });
    }

    private static string ProfileConfiguration(string nodeName)
    {
        return
            "proxies:\n"
            + $"  - name: {nodeName}\n"
            + "    type: direct\n"
            + "proxy-groups:\n"
            + "  - name: GLOBAL\n"
            + "    type: select\n"
            + "    proxies:\n"
            + $"      - {nodeName}\n"
            + "rules:\n"
            + "  - MATCH,DIRECT\n";
    }

    private static void WriteCommittedProfile(
        string configurationDirectory,
        string profileId,
        string configurationText)
    {
        string profileDirectory = Path.Combine(configurationDirectory, "profiles", profileId);
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, "config.yaml"), configurationText);
    }

    private static void AssertNoImportSidecars(string profileDirectory)
    {
        Assert.Empty(Directory.GetFiles(profileDirectory, "config.yaml.staging.*"));
        Assert.Empty(Directory.GetFiles(profileDirectory, "config.yaml.backup.*"));
    }

    private sealed class FakeCoreConfigurationSettings : ICoreConfigurationSettings
    {
        public bool TransparentProxyEnabled { get; init; }

        public int MixedPort { get; init; } = 7890;

        public string ActiveProfileId { get; init; } = ProfileCatalogIds.BuiltInDirect;
    }

    private sealed class FakeCoreConfigurationProfileMetrics : ICoreConfigurationProfileMetrics
    {
        public int NodeCount { get; init; }

        public int RuleCount { get; init; }

        public int CountNodes(string configurationText)
        {
            return NodeCount;
        }

        public int CountRules(string configurationText)
        {
            return RuleCount;
        }
    }

    private sealed class CoordinatedProfileMetrics : ICoreConfigurationProfileMetrics
    {
        public ManualResetEventSlim CountingCompleted { get; } = new();

        public int CountNodes(string configurationText)
        {
            return 2;
        }

        public int CountRules(string configurationText)
        {
            CountingCompleted.Set();
            return 2;
        }
    }

    private sealed class MarkerProfileMetrics(
        string expectedMarker,
        int nodeCount,
        int ruleCount) : ICoreConfigurationProfileMetrics
    {
        public int CountNodes(string configurationText)
        {
            Assert.Contains(expectedMarker, configurationText, StringComparison.Ordinal);
            return nodeCount;
        }

        public int CountRules(string configurationText)
        {
            Assert.Contains(expectedMarker, configurationText, StringComparison.Ordinal);
            return ruleCount;
        }
    }

    private sealed class BlockingProfileTextIo : IDisposable
    {
        private int _readsToBlock = 1;

        public ManualResetEventSlim ReadEntered { get; } = new();

        public ManualResetEventSlim AllowRead { get; } = new();

        public ManualResetEventSlim WriteEntered { get; } = new();

        public string ReadAllText(string path)
        {
            if (Interlocked.Exchange(ref _readsToBlock, 0) == 1)
            {
                ReadEntered.Set();
                Assert.True(AllowRead.Wait(TimeSpan.FromSeconds(5)));
            }

            return File.ReadAllText(path);
        }

        public void WriteAllText(string path, string contents, Encoding encoding)
        {
            WriteEntered.Set();
            File.WriteAllText(path, contents, encoding);
        }

        public void Dispose()
        {
            AllowRead.Set();
            ReadEntered.Dispose();
            AllowRead.Dispose();
            WriteEntered.Dispose();
        }
    }

    private sealed class FakeCoreConfigurationValidator : ICoreConfigurationValidator
    {
        public List<CoreValidationRequest> Requests { get; } = [];

        public Task ValidateAsync(string workingDirectory, string configurationPath, CancellationToken cancellationToken)
        {
            Requests.Add(new CoreValidationRequest(workingDirectory, configurationPath));
            return Task.CompletedTask;
        }
    }

    private sealed class CoordinatedImportValidator : ICoreConfigurationValidator
    {
        private readonly ConcurrentDictionary<string, ValidationStep> _steps =
            new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<CoreValidationRequest> _requests = new();

        public IReadOnlyList<CoreValidationRequest> Requests => _requests.ToArray();

        public ValidationStep AddBlockedFailure(string marker, Exception failure)
        {
            return Add(marker, new ValidationStep(waitForRelease: true, failure, waitForCancellation: false));
        }

        public ValidationStep AddBlockedSuccess(string marker)
        {
            return Add(marker, new ValidationStep(waitForRelease: true, failure: null, waitForCancellation: false));
        }

        public ValidationStep AddCancellationWait(string marker)
        {
            return Add(marker, new ValidationStep(waitForRelease: false, failure: null, waitForCancellation: true));
        }

        public void AddImmediateSuccess(string marker)
        {
            Add(marker, new ValidationStep(waitForRelease: false, failure: null, waitForCancellation: false));
        }

        public void AddImmediateFailure(string marker, Exception failure)
        {
            Add(marker, new ValidationStep(waitForRelease: false, failure, waitForCancellation: false));
        }

        public Task ValidateAsync(
            string workingDirectory,
            string configurationPath,
            CancellationToken cancellationToken)
        {
            string configurationText = File.ReadAllText(configurationPath);
            KeyValuePair<string, ValidationStep> match = Assert.Single(
                _steps,
                step => configurationText.Contains(
                    $"name: {step.Key}",
                    StringComparison.Ordinal));
            _requests.Enqueue(new CoreValidationRequest(workingDirectory, configurationPath));
            match.Value.MarkEntered(configurationPath);
            return match.Value.ExecuteAsync(cancellationToken);
        }

        private ValidationStep Add(string marker, ValidationStep step)
        {
            Assert.True(_steps.TryAdd(marker, step));
            return step;
        }
    }

    private sealed class ImportThenValidateValidator : ICoreConfigurationValidator
    {
        private readonly TaskCompletionSource _releaseImport =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _validationCount;

        public TaskCompletionSource ImportEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? ValidatedCommittedText { get; private set; }

        public async Task ValidateAsync(
            string workingDirectory,
            string configurationPath,
            CancellationToken cancellationToken)
        {
            int validationCount = Interlocked.Increment(ref _validationCount);
            if (validationCount == 1)
            {
                Assert.Contains(".staging.", configurationPath, StringComparison.Ordinal);
                ImportEntered.TrySetResult();
                await _releaseImport.Task.WaitAsync(cancellationToken);
                return;
            }

            Assert.Equal(Path.Combine(workingDirectory, "config.yaml"), configurationPath);
            ValidatedCommittedText = await File.ReadAllTextAsync(
                configurationPath,
                cancellationToken);
        }

        public void ReleaseImport()
        {
            _releaseImport.TrySetResult();
        }
    }

    private sealed class ValidationStep(
        bool waitForRelease,
        Exception? failure,
        bool waitForCancellation)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkEntered(string path)
        {
            Entered.TrySetResult(path);
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            if (waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            if (waitForRelease)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clashsharp-core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private readonly record struct CoreValidationRequest(string WorkingDirectory, string ConfigurationPath);
}
