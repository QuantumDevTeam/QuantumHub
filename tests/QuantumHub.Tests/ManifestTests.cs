using QuantumHub.Core.Plugins;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Разбор и проверка манифестов.</summary>
public class ManifestTests
{
    [Fact]
    public void Reads_jsonc_with_comments_and_trailing_commas()
    {
        var manifest = ManifestReader.Parse("""
            // a comment
            {
              "id": "a.b",
              "version": "1.0.0", /* another */
              "unknownField": 1,
              "dependencies": [ { "capability": "x", "optional": true, "when": "lazy" }, ],
              "settings": [ { "key": "k", "type": "bool", "default": true } ],
            }
            """);

        Assert.Equal("a.b", manifest.Id);
        Assert.Equal("x", manifest.Dependencies[0].Capability);
        Assert.True(manifest.Dependencies[0].Optional);
        Assert.True(manifest.Settings[0].Default.BoolValue);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Rejects_ids_that_are_not_safe_as_file_names(string id) =>
        Assert.Throws<InvalidDataException>(() => ManifestReader.Parse($$"""{ "id": "{{id}}", "version": "1.0.0" }"""));

    [Fact]
    public void Rejects_a_dependency_with_both_id_and_capability() =>
        Assert.Throws<InvalidDataException>(() => ManifestReader.Parse(
            """{ "id": "a", "version": "1.0.0", "dependencies": [ { "id": "b", "capability": "c" } ] }"""));
}
