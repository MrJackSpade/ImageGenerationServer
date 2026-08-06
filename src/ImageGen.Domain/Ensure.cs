using ImageGen.Domain.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ImageGen.Domain;

/// <summary>
/// Argument guards that refuse an invalid value and NAME it in the failure. Each method captures the caller's
/// expression via <see cref="CallerArgumentExpressionAttribute"/>, so <c>Ensure.GreaterThanZero(scale)</c> throws
/// "scale must be greater than zero." with no hand-written name to drift out of sync. Pass an explicit
/// <c>name</c> only when the captured expression wouldn't read well — e.g. a param-bag lookup
/// (<c>Ensure.NotNegative(p.Int(k), k)</c> names the key, not the whole <c>p.Int(k)</c> expression).
///
/// <para>This is the single home for the project's invalid-value contract (<c>invalid-values-throw-not-correct</c>):
/// invalid input is REFUSED here, never silently clamped, coerced, or defaulted at the call site. Each guard
/// returns the value it validated, so it drops into an initializer or an argument list without a second statement.</para>
///
/// <para>Every method fits ONLY when the failure is cleanly described by the shared generic message "<c>name</c> must
/// be <c>&lt;condition&gt;</c>". When the surfaced error needs operation-specific context, let the guard throw its clean
/// exception and have the calling layer catch it and rethrow a richer exception with this one as the inner exception —
/// do not jam context-specific prose into a guard message.</para>
/// </summary>
public static class Ensure
{
    /// <summary>The value, or a refusal naming it when it is null.</summary>
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(name, $"{name} must not be null.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is null or empty.</summary>
    public static string NotNullOrEmpty(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"{name} must not be null or empty.", name);
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is null, empty, or whitespace.</summary>
    public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} must not be null or whitespace.", name);
        }

        return value;
    }

    /// <summary>The collection, or a refusal naming it when it is empty.</summary>
    public static IReadOnlyCollection<T> NotEmpty<T>(
        IReadOnlyCollection<T> value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value.Count == 0)
        {
            throw new ArgumentException($"{name} must not be empty.", name);
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it does not equal <paramref name="expected"/>.</summary>
    public static T Equal<T>(T value, T expected, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(value, expected))
        {
            throw new ArgumentException($"{name} must equal {expected}.", name);
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it equals <paramref name="forbidden"/>.</summary>
    public static T NotEqual<T>(T value, T forbidden, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, forbidden))
        {
            throw new ArgumentException($"{name} must not equal {forbidden}.", name);
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is not one of <paramref name="allowed"/>.</summary>
    [AllowMagicStrings("guard message list separator")]
    public static T OneOf<T>(
        T value, IReadOnlyCollection<T> allowed, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{name} must be one of: {string.Join(", ", allowed)}.", name);
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is not a defined <typeparamref name="TEnum"/> member.</summary>
    public static TEnum Defined<TEnum>(TEnum value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be a defined {typeof(TEnum).Name} value.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it equals <c>default(T)</c>.</summary>
    public static T NotDefault<T>(T value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, default))
        {
            throw new ArgumentException($"{name} must not be its default value.", name);
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is not greater than zero.</summary>
    public static int GreaterThanZero(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be greater than zero.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is negative.</summary>
    public static int NotNegative(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must not be negative.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is below <paramref name="min"/>.</summary>
    public static int AtLeast(int value, int min, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be at least {min}.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is above <paramref name="max"/>.</summary>
    public static int AtMost(int value, int max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be at most {max}.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is not below <paramref name="max"/>.</summary>
    public static int LessThan(int value, int max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value >= max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be less than {max}.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is outside [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static int Between(int value, int min, int max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is not greater than zero.</summary>
    public static double GreaterThanZero(double value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be greater than zero.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is negative.</summary>
    public static double NotNegative(double value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must not be negative.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is below <paramref name="min"/>.</summary>
    public static double AtLeast(double value, double min, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be at least {min}.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is above <paramref name="max"/>.</summary>
    public static double AtMost(double value, double max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be at most {max}.");
        }

        return value;
    }

    /// <summary>The value, or a refusal naming it when it is outside [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static double Between(double value, double min, double max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}.");
        }

        return value;
    }
}
