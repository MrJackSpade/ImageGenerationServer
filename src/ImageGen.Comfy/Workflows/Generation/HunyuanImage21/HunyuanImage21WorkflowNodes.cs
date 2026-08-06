using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.HunyuanImage21;

/// <summary>HunyuanImage 2.1's own node ids beyond the inherited txt2img roles (reuses the inherited txt2img
/// <c>Nodes.*</c> for the shared roles).</summary>
internal static class HunyuanImage21WorkflowNodes
{
    /// <summary>This workflow's flow-shift node id.</summary>
    public const string ModelSampling = "30";
}
