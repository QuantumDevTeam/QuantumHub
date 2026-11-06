using QuantumHub.Plugin.Activity;
using QuantumHub.Plugin.SystemSpecs;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Логика наблюдения за активностью и подсчёта загрузки.</summary>
public class ActivityTests
{
    [Fact]
    public void Reports_start_and_exit_of_watched_processes_only()
    {
        var watcher = new ProcessWatcher();
        watcher.Watch(["VRChat"]);

        Assert.Equal([new ProcessChange("VRChat", true)], watcher.Poll(_ => true));
        Assert.Empty(watcher.Poll(_ => true));
        Assert.Equal([new ProcessChange("VRChat", false)], watcher.Poll(_ => false));
    }

    [Fact]
    public void Watching_twice_keeps_the_known_state()
    {
        var watcher = new ProcessWatcher();
        watcher.Watch(["VRChat"]);
        watcher.Poll(_ => true);

        watcher.Watch(["vrchat"]);

        Assert.Empty(watcher.Poll(_ => true));
    }

    [Fact]
    public void Reports_going_idle_and_coming_back_once()
    {
        var tracker = new IdleTracker();
        var threshold = TimeSpan.FromMinutes(2);

        Assert.False(tracker.Update(TimeSpan.FromSeconds(10), threshold));
        Assert.True(tracker.Update(TimeSpan.FromMinutes(3), threshold));
        Assert.False(tracker.Update(TimeSpan.FromMinutes(4), threshold));
        Assert.True(tracker.Update(TimeSpan.Zero, threshold));
    }

    [Theory]
    [InlineData(75ul, 100ul, 25)]
    [InlineData(100ul, 100ul, 0)]
    [InlineData(0ul, 100ul, 100)]
    [InlineData(0ul, 0ul, 0)]
    [InlineData(150ul, 100ul, 0)]
    public void Cpu_load_comes_from_idle_and_total_deltas(ulong idle, ulong total, double expected) =>
        Assert.Equal(expected, CpuLoad.Percent(idle, total));
}
