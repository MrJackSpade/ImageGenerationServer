using ImageGen.Comfy;

namespace ImageGen.Tests;

/// <summary>Locks the edit working-resolution contract independently of any one workflow graph.</summary>
public sealed class EditWorkingResolutionTests
{
    [Theory]
    [InlineData(512, 512, 1024, 1024)]
    [InlineData(1024, 1024, 1024, 1024)]
    [InlineData(2048, 2048, 1024, 1024)]
    [InlineData(1216, 832, 1232, 848)]
    public void Native_budget_normalizes_small_native_and_large_sources(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            EditWorkingResolution.Resolve(sourceWidth, sourceHeight));
    }

    [Fact]
    public void Long_edge_ceiling_is_applied_after_budget_scaling() =>
        // A 4:1 source lands at 2048x512 at 1 MP, then the 1536 safety cap reduces it without changing aspect.
        Assert.Equal((1536, 384), EditWorkingResolution.Resolve(2048, 512, maxDimension: 1536));

    [Theory]
    [InlineData(0, 512)]
    [InlineData(512, 0)]
    public void Rejects_degenerate_sources(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EditWorkingResolution.Resolve(width, height));

    [Fact]
    public void Rejects_a_negative_maximum_dimension() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EditWorkingResolution.Resolve(512, 512, maxDimension: -1));
}
