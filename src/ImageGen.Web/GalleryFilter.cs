namespace ImageGen.Web;

/// <summary>
/// Wire tokens for the history page's workflow filter. A <c>&lt;select&gt;</c> option always submits SOME value — it
/// cannot submit "absent" — so the "All workflows" option carries an explicit sentinel the server maps back to "no
/// filter". This is what lets the filter distinguish "no workflow chosen" from "the workflow whose ModelId is the
/// empty string" (the legacy model-scoped Anima group): the sentinel and an absent parameter both mean no filter,
/// while any other value — INCLUDING the empty string — is a real workflow id to filter on. See #188.
/// </summary>
public static class GalleryFilter
{
    /// <summary>The "All workflows" option's value: the no-filter sentinel. Never a real configuration id (those are
    /// non-empty slugs; the only unusual real id is the empty string, which this deliberately is not).</summary>
    public const string AllWorkflows = "__all__";
}
