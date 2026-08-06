namespace ImageGen.Comfy.Generation.HunyuanVideo15T2V;

/// <summary>Picks <see cref="HunyuanVideo15T2VSrParams"/> vs <see cref="HunyuanVideo15T2VNoSrParams"/> by the <c>sr</c> toggle.</summary>
public sealed class HunyuanVideo15T2VParamsConverter
    : HunyuanSrToggleConverter<HunyuanVideo15T2VParams, HunyuanVideo15T2VSrParams, HunyuanVideo15T2VNoSrParams>;
