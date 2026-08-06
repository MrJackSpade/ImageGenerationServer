namespace ImageGen.Comfy;

/// <summary>Mage-Flow's own node id: its unified text-encode + zero-latent node, emitting the (positive, negative,
/// latent) triple. Reuses the inherited txt2img <c>Nodes.*</c> for the shared roles.</summary>
internal static class MageFlowGenBaseNodes
{
    public const string Encode = "5";
}
