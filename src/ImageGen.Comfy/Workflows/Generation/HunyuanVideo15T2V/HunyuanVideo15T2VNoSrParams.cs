using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.HunyuanVideo15T2V;

/// <summary>The t2v params for a config with NO super-resolution pass.</summary>
public sealed record HunyuanVideo15T2VNoSrParams : HunyuanVideo15T2VParams;
