extern alias ClashSharpUi;
using ClashSharp.Model;
using ProxyModeAnnouncer = ClashSharpUi::ClashSharp.Service.ProxyModeAnnouncer;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>
/// What a verified proxy mode tells the user: a mode-change notification only for a real change, and the
/// "proxy started" trigger event only when traffic takeover begins, however the mode was applied.
/// </summary>
public sealed class ProxyModeAnnouncerTests
{
    private static readonly ClashSharpMode[] Modes =
        [ClashSharpMode.Disabled, ClashSharpMode.Standby, ClashSharpMode.RuleTakeover, ClashSharpMode.FullTakeover];

    [Fact]
    public void ReselectingTheActiveModeIsSilent()
    {
        foreach (ClashSharpMode mode in Modes)
        {
            ProxyModeAnnouncer announcer = new();
            _ = announcer.Observe(mode);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                Assert.Equal((false, false), announcer.Observe(mode));
            }
        }
    }

    [Fact]
    public void TheFirstReportIsMeasuredAgainstNothingRunning()
    {
        Assert.Equal((false, false), new ProxyModeAnnouncer().Observe(ClashSharpMode.Disabled));
        Assert.Equal((true, false), new ProxyModeAnnouncer().Observe(ClashSharpMode.Standby));
        Assert.Equal((true, true), new ProxyModeAnnouncer().Observe(ClashSharpMode.RuleTakeover));
        Assert.Equal((true, true), new ProxyModeAnnouncer().Observe(ClashSharpMode.FullTakeover));
    }

    [Theory]
    [InlineData(ClashSharpMode.Disabled, ClashSharpMode.RuleTakeover, true)]
    [InlineData(ClashSharpMode.Standby, ClashSharpMode.FullTakeover, true)]
    [InlineData(ClashSharpMode.RuleTakeover, ClashSharpMode.FullTakeover, false)]
    [InlineData(ClashSharpMode.FullTakeover, ClashSharpMode.RuleTakeover, false)]
    [InlineData(ClashSharpMode.RuleTakeover, ClashSharpMode.Standby, false)]
    [InlineData(ClashSharpMode.FullTakeover, ClashSharpMode.Disabled, false)]
    public void TakeoverStartsOnlyWhenTrafficWasNotAlreadyTakenOver(ClashSharpMode from, ClashSharpMode to, bool started)
    {
        ProxyModeAnnouncer announcer = new();
        _ = announcer.Observe(from);
        Assert.Equal((true, started), announcer.Observe(to));
    }

    /// <summary>
    /// Over random mode sequences: a notification exactly when the mode differs from the last report, a start event
    /// only together with a notification and only on entering takeover, and never two starts without leaving
    /// takeover in between.
    /// </summary>
    [Fact]
    public void RandomSequencesAnnounceExactlyTheTransitions()
    {
        Random random = new(20261110);
        for (int run = 0; run < 300; run++)
        {
            ProxyModeAnnouncer announcer = new();
            ClashSharpMode previous = ClashSharpMode.Disabled;
            bool takeoverActive = false;
            for (int step = 0; step < 50; step++)
            {
                ClashSharpMode mode = Modes[random.Next(Modes.Length)];
                (bool changed, bool started) = announcer.Observe(mode);
                bool isTakeover = mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;

                Assert.Equal(mode != previous, changed);
                Assert.True(!started || changed, "a start without a change");
                Assert.Equal(isTakeover && !takeoverActive, started);
                takeoverActive = isTakeover;
                previous = mode;
            }
        }
    }

    [Fact]
    public void ConcurrentReportsOfOneChangeAnnounceItOnce()
    {
        ProxyModeAnnouncer announcer = new();
        _ = announcer.Observe(ClashSharpMode.Disabled);
        int changes = 0;
        int starts = 0;
        Parallel.For(0, 64, _ =>
        {
            (bool changed, bool started) = announcer.Observe(ClashSharpMode.RuleTakeover);
            if (changed) { Interlocked.Increment(ref changes); }
            if (started) { Interlocked.Increment(ref starts); }
        });

        Assert.Equal(1, changes);
        Assert.Equal(1, starts);
    }
}
