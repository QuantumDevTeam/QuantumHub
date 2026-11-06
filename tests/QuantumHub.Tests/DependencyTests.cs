using QuantumHub.Contracts.Proto;
using QuantumHub.Core.Plugins;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Версии и подбор поставщиков: чистая логика без запуска хаба.</summary>
public class DependencyTests
{
    private static ProviderCandidate Plugin(string id, string version, params (string Name, string Version)[] provides) =>
        new(id, version, provides.Select(p => new Capability { Name = p.Name, Version = p.Version }).ToList());

    [Theory]
    [InlineData("*", "1.2.3", true)]
    [InlineData("", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData(">=1.0.0", "1.0.0", true)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    [InlineData(">=1.0.0 <2.0.0", "1.9.9", true)]
    [InlineData(">=1.0.0 <2.0.0", "2.0.0", false)]
    [InlineData("^1.2.0", "1.9.0", true)]
    [InlineData("^1.2.0", "2.0.0", false)]
    [InlineData("^1.2.0", "1.1.9", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("~1.2.0", "1.2.9", true)]
    [InlineData("~1.2.0", "1.3.0", false)]
    [InlineData(">=1.0.0", "1.0.0-beta", false)]
    public void Version_ranges(string range, string version, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed));
        Assert.Equal(expected, parsed.Includes(SemVersion.Parse(version)));
    }

    [Theory]
    [InlineData(">=abc")]
    [InlineData("^")]
    public void Bad_version_ranges_are_rejected(string range) => Assert.False(VersionRange.TryParse(range, out _));

    [Fact]
    public void Finds_a_plugin_by_id()
    {
        var result = DependencyResolver.Match(new Dependency { Id = "a", Version = ">=1.0.0" }, [Plugin("a", "1.5.0")]);

        Assert.Equal(DependencyState.Resolved, result.State);
        Assert.Equal("a", result.ProviderId);
        Assert.Equal("1.5.0", result.ProviderVersion);
    }

    [Fact]
    public void Reports_a_missing_plugin() =>
        Assert.Equal(DependencyState.Missing, DependencyResolver.Match(new Dependency { Id = "a" }, [Plugin("b", "1.0.0")]).State);

    [Fact]
    public void Reports_a_version_mismatch_rather_than_missing()
    {
        var result = DependencyResolver.Match(new Dependency { Id = "a", Version = ">=2.0.0" }, [Plugin("a", "1.0.0")]);

        Assert.Equal(DependencyState.VersionMismatch, result.State);
        Assert.Contains("1.0.0", result.Detail);
    }

    [Fact]
    public void Picks_the_highest_version_among_capability_providers()
    {
        var result = DependencyResolver.Match(new Dependency { Capability = "c" },
            [Plugin("old", "9.0.0", ("c", "1.0.0")), Plugin("new", "1.0.0", ("c", "2.0.0"))]);

        Assert.Equal("new", result.ProviderId);
        Assert.Equal("2.0.0", result.ProviderVersion);
    }

    [Fact]
    public void Skips_an_unavailable_provider_when_another_one_works()
    {
        var disabled = Plugin("best", "1.0.0", ("c", "3.0.0")) with { UnavailableReason = "the plugin is disabled" };
        var result = DependencyResolver.Match(new Dependency { Capability = "c" }, [disabled, Plugin("ok", "1.0.0", ("c", "1.0.0"))]);

        Assert.Equal("ok", result.ProviderId);
    }

    [Fact]
    public void A_disabled_only_provider_is_a_failure_with_the_reason()
    {
        var disabled = Plugin("a", "1.0.0") with { UnavailableReason = "the plugin is disabled" };
        var result = DependencyResolver.Match(new Dependency { Id = "a" }, [disabled]);

        Assert.Equal(DependencyState.Failed, result.State);
        Assert.Contains("disabled", result.Detail);
    }

    [Fact]
    public void Finds_a_cycle_and_names_it()
    {
        var edges = new Dictionary<string, string[]> { ["a"] = ["b"], ["b"] = ["c"], ["c"] = ["a"] };

        Assert.Equal(["a", "b", "c", "a"], DependencyResolver.FindCycle("a", id => edges[id]));
    }

    [Fact]
    public void A_diamond_is_not_a_cycle()
    {
        var edges = new Dictionary<string, string[]> { ["a"] = ["b", "c"], ["b"] = ["d"], ["c"] = ["d"], ["d"] = [] };

        Assert.Null(DependencyResolver.FindCycle("a", id => edges[id]));
    }
}
