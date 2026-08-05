using ImageGen.Domain;

namespace ImageGen.Tests;

/// <summary>
/// The shared argument guards: each passes a valid value straight through and refuses an out-of-range one with a
/// message that NAMES the value (captured from the caller's expression). This is the single home of the project's
/// invalid-values-throw-not-correct contract, so it is pinned directly.
/// </summary>
public sealed class EnsureTests
{
    [Fact]
    public void GreaterThanZero_returns_the_value_when_positive() =>
        Assert.Equal(3, Ensure.GreaterThanZero(3));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void GreaterThanZero_refuses_zero_and_negatives(int bad) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.GreaterThanZero(bad));

    [Fact]
    public void NotNegative_allows_zero_but_refuses_negatives()
    {
        Assert.Equal(0, Ensure.NotNegative(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.NotNegative(-1));
    }

    [Fact]
    public void AtLeast_enforces_the_floor()
    {
        Assert.Equal(5, Ensure.AtLeast(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.AtLeast(4, 5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Between_int_allows_the_inclusive_bounds(int ok) =>
        Assert.Equal(ok, Ensure.Between(ok, 1, 200));

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Between_int_refuses_outside(int bad) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.Between(bad, 1, 200));

    [Fact]
    public void Between_double_refuses_outside_and_returns_inside()
    {
        Assert.Equal(2.5, Ensure.Between(2.5, 0.0, 5.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.Between(5.1, 0.0, 5.0));
    }

    [Fact]
    public void Failure_names_the_captured_expression()
    {
        int scale = 0;
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.GreaterThanZero(scale));
        Assert.Equal("scale", ex.ParamName);
        Assert.StartsWith("scale must be greater than zero.", ex.Message);
    }

    [Fact]
    public void AtMost_enforces_the_ceiling()
    {
        Assert.Equal(5, Ensure.AtMost(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.AtMost(6, 5));
    }

    [Fact]
    public void LessThan_refuses_the_bound_itself()
    {
        Assert.Equal(4, Ensure.LessThan(4, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.LessThan(5, 5));
    }

    [Fact]
    public void Double_range_overloads_resolve_and_enforce()
    {
        Assert.Equal(2.0, Ensure.GreaterThanZero(2.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.GreaterThanZero(0.0));
        Assert.Equal(0.0, Ensure.NotNegative(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.NotNegative(-0.1));
        Assert.Equal(1.5, Ensure.AtLeast(1.5, 1.5));
        Assert.Equal(1.5, Ensure.AtMost(1.5, 1.5));
    }

    [Fact]
    public void NotNull_returns_the_value_or_throws()
    {
        object o = new object();
        Assert.Same(o, Ensure.NotNull(o));
        Assert.Throws<ArgumentNullException>(() => Ensure.NotNull((string?)null));
    }

    [Fact]
    public void NotNullOrEmpty_refuses_null_and_empty()
    {
        Assert.Equal("x", Ensure.NotNullOrEmpty("x"));
        Assert.Throws<ArgumentException>(() => Ensure.NotNullOrEmpty(""));
        Assert.Throws<ArgumentException>(() => Ensure.NotNullOrEmpty(null));
    }

    [Fact]
    public void NotNullOrWhiteSpace_refuses_whitespace()
    {
        Assert.Equal("x", Ensure.NotNullOrWhiteSpace("x"));
        Assert.Throws<ArgumentException>(() => Ensure.NotNullOrWhiteSpace("   "));
    }

    [Fact]
    public void NotEmpty_refuses_an_empty_collection()
    {
        int[] some = [1];
        Assert.Same(some, Ensure.NotEmpty(some));
        Assert.Throws<ArgumentException>(() => Ensure.NotEmpty(Array.Empty<int>()));
    }

    [Fact]
    public void Equal_and_NotEqual_enforce_the_relation()
    {
        Assert.Equal(3, Ensure.Equal(3, 3));
        Assert.Throws<ArgumentException>(() => Ensure.Equal(3, 4));
        Assert.Equal(3, Ensure.NotEqual(3, 4));
        Assert.Throws<ArgumentException>(() => Ensure.NotEqual(3, 3));
    }

    [Fact]
    public void OneOf_enforces_membership()
    {
        int[] allowed = [1, 2, 3];
        Assert.Equal(2, Ensure.OneOf(2, allowed));
        Assert.Throws<ArgumentException>(() => Ensure.OneOf(9, allowed));
    }

    [Fact]
    public void Defined_refuses_an_undefined_enum_value()
    {
        Assert.Equal(Sample.B, Ensure.Defined(Sample.B));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.Defined((Sample)99));
    }

    [Fact]
    public void NotDefault_refuses_the_default()
    {
        Assert.Equal(7, Ensure.NotDefault(7));
        Assert.Throws<ArgumentException>(() => Ensure.NotDefault(0));
        Assert.Throws<ArgumentException>(() => Ensure.NotDefault(Sample.A));
    }

    private enum Sample
    {
        A = 0,
        B = 1,
    }
}
