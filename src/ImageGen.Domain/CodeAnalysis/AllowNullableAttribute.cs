namespace ImageGen.Domain.CodeAnalysis;

/// <summary>
/// Opts the annotated property or field out of the nullable-value-type ban (<c>IMGNULL003</c>).
///
/// <para>The ban exists because a nullable value type (<c>int?</c>, <c>double?</c>, <c>bool?</c>,
/// <c>DateTime?</c>, an <c>enum?</c>, …) that is nullable only because the caller might omit it is a defect: it
/// should be a non-nullable property with a default so the value is guaranteed at the DTO/API boundary and no
/// downstream layer has to <c>?? default</c> it. Apply this attribute only where <c>null</c> carries a meaning no
/// default can express — a <c>DateTime? FinishedAtUtc</c> where <c>null</c> means <i>not finished</i> and
/// <c>default(DateTime)</c> would falsely read as "finished in year 1", or a <c>double? ChangeScore</c> where
/// <c>null</c> means <i>not computed</i> and <c>0.0</c> is a real score.</para>
///
/// <para>A <paramref name="justification"/> is required — the constructor takes no parameterless form, so the
/// compiler rejects a bare <c>[AllowNullable]</c>, and <c>IMGNULL004</c> rejects an empty or whitespace one.
/// State what <c>null</c> means and why no default can stand in for it; never apply it merely to route around
/// giving an optional input a default.</para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = false)]
public sealed class AllowNullableAttribute : Attribute
{
    /// <param name="justification">What <c>null</c> means here and why no default can express it. Must be non-empty.</param>
    public AllowNullableAttribute(string justification) => Justification = justification;

    /// <summary>The reason this member is allowed to be a nullable value type.</summary>
    public string Justification { get; }
}
