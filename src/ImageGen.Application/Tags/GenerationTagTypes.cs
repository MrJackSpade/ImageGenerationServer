using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Application.Tags;

/// <summary>
/// The <b>generation mask</b>: which Gelbooru tag TYPES the tag model may emit when it generates a random prompt.
/// A type the mask leaves out is suppressed inside the model — its probability is zeroed at every sampling step, so
/// the set completes to a real alternative instead of coming back one tag short — and the model is *told* it is off,
/// so its completeness head stops at the right place.
///
/// EVERY type the model can suppress is switchable. The model is conditioned on every drop-subset of all five
/// member-bearing categories (<c>tagmodel/s2srec2/typemask.py</c>, <c>DROPPABLE</c>) — <c>general</c> included — so
/// the five below ARE that list, and the user's selection is therefore the whole wire list. <c>general</c> off is a
/// real condition, not a broken one: it is how a set like <c>{traditional_media, colored_pencil}</c> gets to be a
/// complete thought.
///
/// Stored per user as the JSON this class parses/serialises; <b>null means unset</b>, which resolves to
/// <see cref="Default"/> — artists off, everything else on (an artist is a style, not a subject, and the composer
/// picks one with its own '@artist' toggle).
/// </summary>
public static class GenerationTagTypes
{
    /// <summary>The switchable types, in display order. Names are the tag model's own category names — they cross the
    /// wire verbatim as <c>types=</c>, so an addition here must exist in the model's <c>DROPPABLE</c> too (it 400s
    /// otherwise), and a category the model gains must be added HERE in the same change: <c>types=</c> names what stays
    /// ALLOWED, so one this list forgets is one the model silently switches OFF.</summary>
    public static readonly IReadOnlyList<string> Selectable = new[] { "general", "artist", "character", "copyright", "meta" };

    /// <summary>What an unset mask means: everything switchable except artists.</summary>
    public static readonly IReadOnlyList<string> Default = new[] { "general", "character", "copyright", "meta" };

    /// <summary>The allowed-type list to put on the tag model's <c>types=</c> parameter. Now that every suppressible
    /// category is switchable this is the selection itself — but callers still go through here rather than passing the
    /// selection straight to the wire, because this is the one place that has to change if the model ever gains a
    /// category the user is offered no switch for. Sending a list short one category does not error; it just quietly
    /// generates under a mask nobody chose.</summary>
    public static IReadOnlyList<string> ForWire(IReadOnlyList<string> selected) => selected;

    /// <summary>Stored-form version. v1 was a bare JSON array written when <c>general</c> was not switchable; v2 is an
    /// object, and the version tag is what keeps "saved before general existed" distinguishable from "deliberately
    /// turned general off" — the two are the same array of four names.</summary>
    private const int StoredVersion = 2;

    private const string GeneralType = "general";
    private const string ListSeparator = ", ";

    /// <summary>The types <paramref name="storedJson"/> allows — <see cref="Default"/> when it is null/blank (unset).
    /// A stored value that is not a known form holding known type names THROWS: it can only be corruption or a rename
    /// that skipped its migration, and silently generating under the wrong mask would put tags in prompts that the user
    /// switched off.
    ///
    /// A v1 value (bare array) is UPGRADED on read, never taken at face value: it was written when <c>general</c> was
    /// always allowed and unlistable, so reading it under today's rules would switch general off for every user who had
    /// ever touched this setting. It is rewritten in v2 form the next time the user saves.</summary>
    public static IReadOnlyList<string> Resolve(string? storedJson)
    {
        if (string.IsNullOrWhiteSpace(storedJson)) return Default;

        string[]? parsed;
        bool legacy;
        try
        {
            using var doc = JsonDocument.Parse(storedJson);
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Array:                       // v1: the switchable-four selection, general implicit
                    parsed = doc.RootElement.Deserialize<string[]>();
                    legacy = true;
                    break;
                case JsonValueKind.Object:
                    var stored = doc.RootElement.Deserialize<StoredMask>();
                    if (stored is null || stored.V != StoredVersion)
                        throw new InvalidOperationException($"Stored generation mask has an unknown version: {storedJson}");
                    parsed = stored.Types;
                    legacy = false;
                    break;
                default:
                    throw new InvalidOperationException($"Stored generation mask is not an array or object: {storedJson}");
            }
        }
        catch (JsonException ex) { throw new InvalidOperationException($"Stored generation mask is not JSON: {storedJson}", ex); }
        if (parsed is null) throw new InvalidOperationException($"Stored generation mask holds no types: {storedJson}");
        if (legacy) parsed = parsed.Append(GeneralType).ToArray();
        if (!TryNormalize(parsed, out var types, out var error)) throw new InvalidOperationException($"Stored generation mask is invalid: {error}");
        return types;
    }

    /// <summary>The v2 stored shape. Property names are pinned so the stored value never depends on whatever naming
    /// policy the host's serializer happens to carry — this text lives in the database and outlives any of that.</summary>
    private sealed record StoredMask(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("types")] string[]? Types);

    /// <summary>Canonicalise a requested selection: known names only, de-duplicated, in <see cref="Selectable"/> order.
    /// False (with <paramref name="error"/>) on an unknown name — the caller answers 400 rather than dropping the token,
    /// since a dropped name reads as "switched off" and quietly changes what gets generated. An EMPTY selection is
    /// valid: it means every switchable type is off.</summary>
    public static bool TryNormalize(IEnumerable<string>? requested, out IReadOnlyList<string> types, out string? error)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested ?? Enumerable.Empty<string>())
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (!Selectable.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                types = Default;
                error = $"unknown tag type '{name}'; the switchable types are {string.Join(ListSeparator, Selectable)}";
                return false;
            }
            wanted.Add(name);
        }
        types = Selectable.Where(wanted.Contains).ToList();
        error = null;
        return true;
    }

    /// <summary>The stored form of a normalized selection: the versioned v2 object. Always explicit — even the empty
    /// selection is stored (as an empty <c>types</c>), which is what keeps "none of them" distinct from unset — and
    /// always versioned, so a selection that omits <c>general</c> reads back as exactly that instead of being mistaken
    /// for a v1 value written before general was switchable.</summary>
    public static string Serialize(IReadOnlyList<string> types) =>
        JsonSerializer.Serialize(new StoredMask(StoredVersion, types.ToArray()));
}
