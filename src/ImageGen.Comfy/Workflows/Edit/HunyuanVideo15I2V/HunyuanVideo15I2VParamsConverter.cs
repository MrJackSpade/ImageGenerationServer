using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.HunyuanVideo15I2V;

/// <summary>Picks <see cref="HunyuanVideo15I2VSrParams"/> vs <see cref="HunyuanVideo15I2VNoSrParams"/> by the <c>sr</c> toggle.</summary>
public sealed class HunyuanVideo15I2VParamsConverter
    : HunyuanSrToggleConverter<HunyuanVideo15I2VParams, HunyuanVideo15I2VSrParams, HunyuanVideo15I2VNoSrParams>;
