using ImageGen.Comfy;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

public sealed class LoraMetaPopulationTests
{
    [Fact]
    public void A_hashless_placeholder_is_retried_but_a_hashed_miss_is_complete()
    {
        LoraMeta unresolved = new("folder/model.safetensors", null, [], null, null, DateTime.UtcNow);
        LoraMeta civitaiMiss = unresolved with { Sha256 = "ABC123" };

        Assert.False(LoraMetaPopulator.IsComplete(unresolved));
        Assert.True(LoraMetaPopulator.IsComplete(civitaiMiss));
    }
}
