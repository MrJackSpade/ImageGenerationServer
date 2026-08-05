using System;

namespace ImageGen.Domain.CodeAnalysis;

/// <summary>
/// Opts the annotated element out of the magic-string ban (<c>IMGSTR001</c>).
///
/// <para>On a class, struct, method, or constructor it exempts every string-literal comparison or argument
/// lexically inside it, including nested members. On a <b>parameter</b> it exempts a string literal passed to
/// that parameter at every call site — declare it once on, say, a custom logging method's <c>message</c>
/// parameter instead of repeating the attribute on every caller.</para>
///
/// <para>A <paramref name="justification"/> is required — the constructor takes no parameterless form, so the
/// compiler rejects a bare <c>[AllowMagicStrings]</c>, and <c>IMGSTR002</c> rejects an empty or whitespace one.
/// State why hardcoding the literal is correct here (a test asserting an exact expected value, a wire-format
/// token pinned by a spec); never apply it merely to route around introducing a named constant.</para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Constructor
    | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = false)]
public sealed class AllowMagicStringsAttribute : Attribute
{
    /// <param name="justification">Why hardcoding the literal(s) in this scope is correct. Must be non-empty.</param>
    public AllowMagicStringsAttribute(string justification) => Justification = justification;

    /// <summary>The reason the literals in this scope are allowed to be hardcoded.</summary>
    public string Justification { get; }
}
