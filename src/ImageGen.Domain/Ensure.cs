using System.Runtime.CompilerServices;

namespace ImageGen.Domain;

/// <summary>
/// Argument guards that refuse an out-of-range value and NAME it in the failure. Each method captures the caller's
/// expression via <see cref="CallerArgumentExpressionAttribute"/>, so <c>Ensure.GreaterThanZero(scale)</c> throws
/// "scale must be greater than zero." with no hand-written name to drift out of sync. Pass an explicit
/// <c>name</c> only when the captured expression wouldn't read well — e.g. a param-bag lookup
/// (<c>Ensure.NotNegative(p.Int(k), k)</c> names the key, not the whole <c>p.Int(k)</c> expression).
///
/// <para>This is the single home for the project's invalid-value contract (<c>invalid-values-throw-not-correct</c>):
/// out-of-range input is REFUSED here, never silently clamped, coerced, or defaulted at the call site. Each guard
/// returns the value it validated, so it drops into an initializer or an argument list without a second statement.</para>
/// </summary>
public static class Ensure
{
    /// <summary>The value, or a refusal naming it when it is not greater than zero.</summary>
    public static int GreaterThanZero(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be greater than zero.");
        return value;
    }

    /// <summary>The value, or a refusal naming it when it is negative.</summary>
    public static int NotNegative(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must not be negative.");
        return value;
    }

    /// <summary>The value, or a refusal naming it when it is below <paramref name="min"/>.</summary>
    public static int AtLeast(int value, int min, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be at least {min}.");
        return value;
    }

    /// <summary>The value, or a refusal naming it when it is outside [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static int Between(int value, int min, int max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}.");
        return value;
    }

    /// <inheritdoc cref="Between(int,int,int,string)"/>
    public static double Between(double value, double min, double max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}.");
        return value;
    }
}
