using System.ComponentModel.DataAnnotations;

namespace ImageGen.Comfy;

/// <summary>Validates an explicit seed while preserving the full non-negative 64-bit range accepted by workflows.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SeedRangeAttribute() : RangeAttribute(typeof(long), "0", "9223372036854775807");
