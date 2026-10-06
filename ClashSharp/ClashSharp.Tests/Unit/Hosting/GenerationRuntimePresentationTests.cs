extern alias ClashSharpUi;

using ClashSharp.Model;
using GenerationPresentation = ClashSharpUi::ClashSharp.Hosting.Data.GenerationRuntimePresentation;

namespace ClashSharp.Tests.Unit.Hosting;

public sealed class GenerationRuntimePresentationTests
{
    [Fact]
    public async Task LoginRecovery_DoesNotResolveTheMissingWindowOrExecuteAppearanceCallbacks()
    {
        int windowReads = 0;
        var presentation = GenerationPresentation.Select(true,
            () => { ++windowReads; throw new InvalidOperationException("The generation startup window is unavailable."); },
            () => false);
        Assert.Equal(0, windowReads);
        int callbacks = 0;
        await using var dispatcher = presentation.CreateDispatcher();
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => ++callbacks, CancellationToken.None));
        Assert.Equal(0, callbacks);
        Assert.Throws<InvalidOperationException>(() => presentation.Appearance.CaptureConfiguration());
        Assert.Throws<InvalidOperationException>(() => presentation.Appearance.ApplyLanguage(AppLanguage.English));
        Assert.Throws<InvalidOperationException>(() => presentation.Appearance.ApplyTheme(AppThemeMode.Dark));
        Assert.Throws<InvalidOperationException>(() => presentation.Appearance.ApplyAccent(new(AppAccentColorMode.Custom, "#804477AA")));
    }

    [Fact]
    public void NormalStartup_MissingWindowStillRejectsGenerationComposition()
    {
        var expected = new InvalidOperationException("The generation startup window is unavailable.");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            GenerationPresentation.Select(false, () => throw expected, () => false)));
    }

    [Fact]
    public void LoginRecovery_UsesTheLiveLifetimeRequestInsteadOfAnAlwaysStoppedOrRunningValue()
    {
        bool exitRequested = false;
        var presentation = GenerationPresentation.Select(true, () => throw new InvalidOperationException("Must not resolve a window."), () => exitRequested);
        Assert.False(presentation.ExitRequested());
        exitRequested = true;
        Assert.True(presentation.ExitRequested());
    }

    [Fact]
    public void NormalStartup_PreservesTheActualWindowBoundariesAndItsLifetime()
    {
        int reads = 0;
        var expected = GenerationPresentation.Select(true, () => throw new InvalidOperationException(), () => false);
        var actual = GenerationPresentation.Select(false, () => { ++reads; return expected; }, () => true);
        Assert.Same(expected, actual);
        Assert.Equal(1, reads);
        Assert.False(actual.ExitRequested());
    }

    [Fact]
    public async Task LoginRecovery_DispatcherCancellationAndRetirementNeverRunTheCallback()
    {
        var presentation = GenerationPresentation.Select(true, () => throw new InvalidOperationException(), () => false);
        var dispatcher = presentation.CreateDispatcher();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.InvokeAsync(() => ++calls, new CancellationToken(true)));
        await dispatcher.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => ++calls, CancellationToken.None));
        Assert.Equal(0, calls);
    }
}
