using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.HunyuanVideo15I2V;

/// <summary>The i2v params for a config with NO super-resolution pass.</summary>
public sealed record HunyuanVideo15I2VNoSrParams : HunyuanVideo15I2VParams;
