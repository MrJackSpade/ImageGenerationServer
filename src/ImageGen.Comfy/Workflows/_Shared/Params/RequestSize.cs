using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>
/// Reads a request's explicit render size and tells a genuine CUSTOM size apart from an aspect resolution. Shared by
/// the submit-path validation (<see cref="WorkflowCatalogService"/>) and the enqueue-pass custom-size snap
/// (<see cref="ComfyClient.NormalizeForQueue"/>, #212), so both agree on what counts as "the composer wrote a clicked
/// shape's dims" versus "the caller typed an arbitrary size".
/// </summary>
internal static class RequestSize
{
    /// <summary>True when the overrides carry BOTH an explicit width and height (a custom size); the pair is returned.</summary>
    public static bool TryExplicit(IReadOnlyDictionary<string, JsonElement>? overrides, out int w, out int h)
    {
        h = 0;
        w = 0;
        return overrides is not null
            && overrides.TryGetValue(WorkflowParamKeys.Width, out JsonElement wEl) && TryPixel(wEl, out w)
            && overrides.TryGetValue(WorkflowParamKeys.Height, out JsonElement hEl) && TryPixel(hEl, out h);
    }

    /// <summary>True when (<paramref name="w"/>,<paramref name="h"/>) exactly matches one of the config's aspect-map
    /// entries (this machine's override applied) — the fingerprint of the composer having written a clicked shape's
    /// dims, as opposed to an arbitrary custom size.</summary>
    public static bool MatchesAspectDims(WorkflowConfiguration cfg, IReadOnlyDictionary<string, JsonElement> machine, int w, int h)
    {
        Dictionary<string, int[]>? map = BuildAspectMap(cfg, machine);
        if (map is null)
        {
            return false;
        }

        foreach (int[] wh in map.Values)
        {
            if (wh.Length >= 2 && wh[0] == w && wh[1] == h)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The configuration's aspect→[w,h] dims map for the composer (#209), machine override applied so the
    /// client writes the same dims the render path would size from. Null when the config declares no aspect map.</summary>
    public static Dictionary<string, int[]>? BuildAspectMap(
        WorkflowConfiguration cfg, IReadOnlyDictionary<string, JsonElement> machine)
    {
        JsonElement el;
        if (machine.TryGetValue(WorkflowParamKeys.Aspect, out JsonElement mEl))
        {
            el = mEl;   // this machine's override wins, exactly as MergeParamsDict overlays it
        }
        else if (cfg.Params.TryGetValue(WorkflowParamKeys.Aspect, out ConfigParam? cp) && cp.Value is JsonElement cEl)
        {
            el = cEl;
        }
        else
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, int[]> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                int[] wh = [.. prop.Value.EnumerateArray().Select(e => e.GetInt32())];
                if (wh.Length >= 2)
                {
                    map[prop.Name] = [wh[0], wh[1]];
                }
            }
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>Read a pixel dimension from an override value — a JSON number, or a numeric string. Anything else is
    /// "not an explicit size" (returns false), left to normal binding rather than mistaken for a custom size.</summary>
    private static bool TryPixel(JsonElement el, out int value)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
        {
            return true;
        }

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
