using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.QwenImageOutpaint;

/// <summary>QwenImageOutpaintWorkflow's own node ids, atop the inherited edit head and QwenInstantXInpaintBase's nodes.</summary>
internal static class Nodes
{
    public const string Pad = "20";
    public const string StretchScale = "21";
    public const string PrefillBlur = "22";
    public const string PrefillComposite = "23";
}
