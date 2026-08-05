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
        var scale = 0;
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Ensure.GreaterThanZero(scale));
        Assert.Equal("scale", ex.ParamName);
        Assert.StartsWith("scale must be greater than zero.", ex.Message);
    }
}
