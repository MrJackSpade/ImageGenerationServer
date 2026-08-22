using ImageGen.Api.Endpoints;
using ImageGen.Application.Images;
using ImageGen.Application.Rendering;
using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

/// <summary>The enqueue boundary treats every edit input id as an owner-scoped image read. Otherwise a foreign id can
/// be copied through a low-denoise edit into a new output the attacker legitimately owns.</summary>
public sealed class EnqueueImageAuthorizationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("source")]
    [InlineData("mask")]
    [InlineData("last-frame")]
    [InlineData("reference")]
    public async Task A_foreign_upload_in_any_edit_input_position_refuses_the_batch(string position)
    {
        const long owner = 7;
        InMemoryUploadStore uploads = new();
        string mine = uploads.Add(Upload(owner));
        string foreign = uploads.Add(Upload(owner + 1));
        EditSpec edit = new(
            "test-edit", "change it", position == "source" ? foreign : mine,
            ReferenceIds: [position == "reference" ? foreign : mine],
            MaskImageId: position == "mask" ? foreign : mine,
            LastFrameImageId: position == "last-frame" ? foreign : mine);
        ImageVisibilityService visibility = new(
            uploads, new FixedStoredVisibility(new HashSet<string>(StringComparer.Ordinal)));

        bool allowed = await ForgeApi.AllEditInputsReadableAsync(
            owner, [RenderItem.ForEdit(edit)], visibility, Ct);

        Assert.False(allowed);
    }

    [Fact]
    public async Task Owned_uploads_and_owned_stored_images_are_accepted_together()
    {
        const long owner = 7;
        InMemoryUploadStore uploads = new();
        string upload = uploads.Add(Upload(owner));
        ImageVisibilityService visibility = new(
            uploads, new FixedStoredVisibility(new HashSet<string>(["stored-mask", "stored-ref"], StringComparer.Ordinal)));
        EditSpec edit = new(
            "test-edit", "change it", upload,
            ReferenceIds: ["stored-ref"], MaskImageId: "stored-mask");

        bool allowed = await ForgeApi.AllEditInputsReadableAsync(
            owner, [RenderItem.ForEdit(edit)], visibility, Ct);

        Assert.True(allowed);
    }

    [Fact]
    public async Task A_generate_only_batch_needs_no_image_grants()
    {
        ImageVisibilityService visibility = new(
            new InMemoryUploadStore(), new FixedStoredVisibility(new HashSet<string>(StringComparer.Ordinal)));
        RenderItem generate = RenderItem.ForGenerate(new GenerateSpec("test", "prompt", null, "square"));

        Assert.True(await ForgeApi.AllEditInputsReadableAsync(7, [generate], visibility, Ct));
    }

    [Fact]
    public async Task Reference_only_edit_authorizes_its_references_without_inventing_a_source_id()
    {
        const long owner = 7;
        InMemoryUploadStore uploads = new();
        string reference = uploads.Add(Upload(owner));
        ImageVisibilityService visibility = new(
            uploads, new FixedStoredVisibility(new HashSet<string>(StringComparer.Ordinal)));
        EditSpec edit = new("qwen-image-edit", "use the reference", null, ReferenceIds: [reference]);

        Assert.True(await ForgeApi.AllEditInputsReadableAsync(
            owner, [RenderItem.ForEdit(edit)], visibility, Ct));
    }

    private static UploadedImage Upload(long owner) => new([1, 2, 3], "image/png", 8, 8, owner);

    private sealed class FixedStoredVisibility(IReadOnlySet<string> readable) : IImageVisibilityRepository
    {
        public Task<bool> IsReadableAsync(long userId, string imageId, CancellationToken ct) =>
            Task.FromResult(readable.Contains(imageId));

        public Task<IReadOnlySet<string>> ReadableAsync(
            long userId, IReadOnlyCollection<string> imageIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(imageIds.Where(readable.Contains).ToHashSet(StringComparer.Ordinal));
    }
}
