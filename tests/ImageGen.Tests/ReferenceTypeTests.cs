using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using ImageGen.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// Issue #154's type-aware references: the kind classifier (a reference's kind is intrinsic to its content type), the
/// per-kind capability model (<see cref="WorkflowReference"/>), and the ref2va card's declaration of image+audio+video.
/// </summary>
public sealed class ReferenceTypeTests
{
    [Theory]
    [InlineData("image/png", ReferenceKind.Image)]
    [InlineData("image/webp", ReferenceKind.Image)]
    [InlineData("audio/wav", ReferenceKind.Audio)]
    [InlineData("audio/mpeg", ReferenceKind.Audio)]
    [InlineData("video/mp4", ReferenceKind.Video)]
    public void A_content_type_classifies_to_its_media_kind(string contentType, ReferenceKind expected) =>
        Assert.Equal(expected, ReferenceKinds.Classify(contentType));

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public void A_non_media_content_type_classifies_to_nothing(string? contentType) =>
        Assert.Null(ReferenceKinds.Classify(contentType));

    [Fact]
    public void A_kind_round_trips_through_its_wire_token()
    {
        foreach (ReferenceKind k in Enum.GetValues<ReferenceKind>())
        {
            Assert.Equal(k, ReferenceKinds.Parse(ReferenceKinds.Wire(k)));
        }
    }

    [Fact]
    public void An_unknown_kind_token_throws_rather_than_defaulting() =>
        Assert.Throws<ArgumentException>(() => ReferenceKinds.Parse("hologram"));

    [Fact]
    public void A_reference_capability_answers_per_kind()
    {
        WorkflowReference r = new([new ReferenceAllowance("image", 3), new ReferenceAllowance("audio", 1)], "hint");
        Assert.True(r.Accepts(ReferenceKind.Image));
        Assert.Equal(3, r.MaxOf(ReferenceKind.Image));
        Assert.True(r.Accepts(ReferenceKind.Audio));
        Assert.Equal(1, r.MaxOf(ReferenceKind.Audio));
        Assert.False(r.Accepts(ReferenceKind.Video));   // not declared → not accepted
        Assert.Equal(0, r.MaxOf(ReferenceKind.Video));
        Assert.Equal(3, r.MaxImages);
    }

    [Fact]
    public void The_ref2va_card_declares_image_audio_and_video_references()
    {
        WorkflowCatalog catalog = new(new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") }, NullLogger<WorkflowCatalog>.Instance);
        WorkflowConfiguration cfg = catalog.FindConfig("minimax-h3-ref2v") ?? throw new Xunit.Sdk.XunitException("ref2v config not found");
        IReadOnlyList<ReferenceAllowance> types = cfg.Card.EditReferenceTypes;

        Assert.Equal(8, types.First(t => t.Kind == "image").Max);
        Assert.Equal(3, types.First(t => t.Kind == "video").Max);
        Assert.Equal(3, types.First(t => t.Kind == "audio").Max);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin dir.");
    }
}
