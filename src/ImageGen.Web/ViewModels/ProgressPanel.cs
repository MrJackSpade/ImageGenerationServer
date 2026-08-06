namespace ImageGen.Web.ViewModels;

/// <summary>
/// The element ids for one render-progress panel — the shared status/ETA/progress-bar/cancel/preview block a page
/// renders via <c>_ProgressPanel.cshtml</c>. Every page (composer, and the edit page's chat/inpaint/outpaint panels)
/// uses the same partial and CSS so they render identically; only the ids differ, because a page may host more than
/// one panel (the edit page has three) and each needs its own bar/eta/cancel/result elements.
/// </summary>
/// <param name="BarId">Id of the progress <c>.bar</c>.</param>
/// <param name="EtaId">Id of the <c>.eta</c> countdown.</param>
/// <param name="CancelId">Id of the inline <c>.cancel-gen</c> × button.</param>
/// <param name="ResultId">Id of the preview/result container.</param>
/// <param name="GenModelId">Optional id of a "generating with X" label rendered under the bar (composer only).</param>
public sealed record ProgressPanel(string BarId, string EtaId, string CancelId, string ResultId, string? GenModelId = null);
