using ImageGen.Api;
using ImageGen.Api.Contracts;
using ImageGen.Domain;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// The tri-state fields stay a boolean-or-absent on the wire even though they are a <see cref="TriState"/> enum in the
/// code: an existing client that sends <c>randomArtist: true|false</c>, or omits it, must be unaffected by the
/// <c>bool?</c>→enum migration. These pin that contract on the one submission shape (<see cref="EnqueueItem"/>),
/// deserializing through the real API options.
/// </summary>
public sealed class TriStateWireTests
{
    [Theory]
    [InlineData("true", TriState.True)]
    [InlineData("false", TriState.False)]
    public void An_explicit_boolean_maps_to_the_matching_state(string json, TriState expected)
    {
        EnqueueItem? req = JsonSerializer.Deserialize<EnqueueItem>(
            $$"""{"workflow":"anima","randomArtist":{{json}},"randomPrompt":{{json}}}""", Json.Options);

        Assert.NotNull(req);
        Assert.Equal(expected, req.RandomArtist);
        Assert.Equal(expected, req.RandomPrompt);
    }

    [Fact]
    public void An_omitted_field_is_Unspecified()
    {
        EnqueueItem? req = JsonSerializer.Deserialize<EnqueueItem>(
            """{"workflow":"anima"}""", Json.Options);

        Assert.NotNull(req);
        Assert.Equal(TriState.Unspecified, req.RandomArtist);
        Assert.Equal(TriState.Unspecified, req.RandomPrompt);
    }

    [Fact]
    public void An_explicit_null_is_Unspecified()
    {
        TriState state = JsonSerializer.Deserialize<TriState>("null");

        Assert.Equal(TriState.Unspecified, state);
    }

    [Theory]
    [InlineData(TriState.True, "true")]
    [InlineData(TriState.False, "false")]
    [InlineData(TriState.Unspecified, "null")]
    public void It_serializes_back_to_a_boolean_or_null_never_an_enum_name(TriState state, string expected) => Assert.Equal(expected, JsonSerializer.Serialize(state));
}
