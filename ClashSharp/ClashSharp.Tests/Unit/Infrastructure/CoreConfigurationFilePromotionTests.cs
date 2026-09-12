using ClashSharp.Infrastructure.Files;

namespace ClashSharp.Tests.Unit.Infrastructure;

public sealed class CoreConfigurationFilePromotionTests
{
    [Theory]
    [InlineData(false, 5)]
    [InlineData(false, 32)]
    [InlineData(false, 33)]
    [InlineData(true, 5)]
    [InlineData(true, 32)]
    [InlineData(true, 33)]
    public async Task TransientNativeFailure_RetriesOnlyPromotionAndPreservesCandidate(bool asynchronous, int error)
    {
        using Candidate candidate = new();
        int attempts = 0;
        await PromoteAsync(asynchronous, candidate, (source, target) =>
        {
            Assert.Equal(candidate.Source, source);
            Assert.Equal(candidate.Target, target);
            Assert.Equal("verified candidate", File.ReadAllText(source));
            Assert.Equal("committed baseline", File.ReadAllText(target));
            if (++attempts <= 2) { throw NativeFailure(error); }
            File.Move(source, target, overwrite: true);
        });

        Assert.Equal(3, attempts);
        Assert.False(File.Exists(candidate.Source));
        Assert.Equal("verified candidate", File.ReadAllText(candidate.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentNativeFailure_IsBoundedAndPreservesFirstFailureForRollback(bool asynchronous)
    {
        using Candidate candidate = new();
        Exception first = NativeFailure(5);
        int attempts = 0;
        IOException failure = await Assert.ThrowsAsync<IOException>(() =>
            PromoteAsync(asynchronous, candidate, (_, _) =>
            {
                attempts++;
                throw attempts == 1 ? first : NativeFailure(5);
            }));

        Assert.Same(first, failure);
        Assert.Equal(6, attempts);
        Assert.Equal("verified candidate", File.ReadAllText(candidate.Source));
        Assert.Equal("committed baseline", File.ReadAllText(candidate.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringRetry_StopsWithoutAnotherPromotion(bool asynchronous)
    {
        using Candidate candidate = new();
        using CancellationTokenSource cancellation = new();
        int attempts = 0;
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PromoteAsync(asynchronous, candidate, (_, _) =>
            {
                attempts++;
                cancellation.Cancel();
                throw NativeFailure(32);
            }, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(1, attempts);
        Assert.Equal("verified candidate", File.ReadAllText(candidate.Source));
        Assert.Equal("committed baseline", File.ReadAllText(candidate.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalGraph_IsPropagatedWithoutRetry(bool asynchronous)
    {
        using Candidate candidate = new();
        Exception fatal = new UnauthorizedAccessException("isolated fatal wrapper", Activator.CreateInstance<OutOfMemoryException>());
        int attempts = 0;
        Exception observed = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            PromoteAsync(asynchronous, candidate, (_, _) =>
            {
                attempts++;
                throw fatal;
            }));

        Assert.Same(fatal, observed);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherIoFailure_IsPropagatedWithoutRetry(bool asynchronous)
    {
        using Candidate candidate = new();
        IOException original = NativeFailure(112);
        int attempts = 0;
        IOException observed = await Assert.ThrowsAsync<IOException>(() =>
            PromoteAsync(asynchronous, candidate, (_, _) =>
            {
                attempts++;
                throw original;
            }));

        Assert.Same(original, observed);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DifferentDirectories_AreRejectedBeforeCallingMove()
    {
        using Candidate candidate = new();
        int attempts = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => CoreConfigurationFilePromotion.PromoteAsync(
            candidate.Source, Path.Combine(candidate.Directory, "other", "target.yaml"), CancellationToken.None,
            (_, _) => attempts++));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task ReadOnlyTarget_RemainsReadOnlyAndRetainsItsCommittedBytes()
    {
        using Candidate candidate = new();
        File.SetAttributes(candidate.Target, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CoreConfigurationFilePromotion.PromoteAsync(
                candidate.Source, candidate.Target, CancellationToken.None));
            Assert.True(File.GetAttributes(candidate.Target).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal("committed baseline", File.ReadAllText(candidate.Target));
            Assert.Equal("verified candidate", File.ReadAllText(candidate.Source));
        }
        finally
        {
            File.SetAttributes(candidate.Target, FileAttributes.Normal);
        }
    }

    private static Task PromoteAsync(bool asynchronous, Candidate candidate, Action<string, string> move,
        CancellationToken cancellationToken = default)
    {
        if (asynchronous)
        {
            return CoreConfigurationFilePromotion.PromoteAsync(candidate.Source, candidate.Target, cancellationToken, move);
        }
        CoreConfigurationFilePromotion.Promote(candidate.Source, candidate.Target, cancellationToken, move);
        return Task.CompletedTask;
    }

    private static IOException NativeFailure(int error) =>
        new($"Isolated Windows file promotion failure {error}.", unchecked((int)0x80070000) | error);

    private sealed class Candidate : IDisposable
    {
        public Candidate()
        {
            Directory = Path.Combine(Path.GetTempPath(), "clashsharp-file-promotion-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Source = Path.Combine(Directory, "config.yaml.staging");
            Target = Path.Combine(Directory, "config.yaml");
            File.WriteAllText(Source, "verified candidate");
            File.WriteAllText(Target, "committed baseline");
        }

        public string Directory { get; }
        public string Source { get; }
        public string Target { get; }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
