using Microsoft.CodeAnalysis;

namespace ImageGen.Analyzers.Tests;

public sealed class MagicStringAnalyzerTests
{
    private const string CanonicalAttribute = """
        namespace ImageGen.Domain.CodeAnalysis
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct
                | System.AttributeTargets.Method | System.AttributeTargets.Constructor
                | System.AttributeTargets.Parameter | System.AttributeTargets.Property | System.AttributeTargets.Field)]
            public sealed class AllowMagicStringsAttribute(string reason) : System.Attribute;
        }
        """;

    [Theory]
    [InlineData("x == $\"steps\"")]
    [InlineData("x == (\"ste\" + \"ps\")")]
    [InlineData("x == (\"steps\" + \"\")")]
    public async Task Literal_built_constant_strings_are_reported(string expression)
    {
        string source = $$"""
            class C
            {
                bool M(string x) => {{expression}};
            }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new MagicStringAnalyzer(), source));
        Assert.Equal(MagicStringAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task Utf8_string_literals_are_reported_as_arguments()
    {
        const string source = """
            using System;
            class C
            {
                static void Take(ReadOnlySpan<byte> value) { }
                void M() => Take("steps"u8);
            }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new MagicStringAnalyzer(), source));
        Assert.Equal(MagicStringAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task A_named_string_constant_is_the_safe_replacement()
    {
        const string source = """
            class C
            {
                private const string Steps = "steps";
                bool M(string x) => x == Steps;
            }
            """;

        Assert.Empty(await AnalyzerTestHarness.AnalyzeAsync(new MagicStringAnalyzer(), source));
    }

    [Fact]
    public async Task The_canonical_attribute_with_a_reason_still_exempts_its_scope()
    {
        string source = CanonicalAttribute + """
            class C
            {
                [ImageGen.Domain.CodeAnalysis.AllowMagicStrings("wire token")]
                bool M(string x) => x == "steps";
            }
            """;

        Assert.Empty(await AnalyzerTestHarness.AnalyzeAsync(new MagicStringAnalyzer(), source));
    }

    [Fact]
    public async Task A_same_named_assembly_attribute_cannot_exempt_the_compilation()
    {
        const string source = """
            using System;
            [assembly: AllowMagicStrings]
            sealed class AllowMagicStringsAttribute : Attribute { }
            class C { bool M(string x) => x == "steps"; }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new MagicStringAnalyzer(), source));
        Assert.Equal(MagicStringAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task A_same_named_parameter_attribute_cannot_exempt_a_call_site()
    {
        const string source = """
            using System;
            sealed class AllowMagicStringsAttribute : Attribute { }
            class C
            {
                static void Take([AllowMagicStrings] string value) { }
                void M() => Take("steps");
            }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new MagicStringAnalyzer(), source));
        Assert.Equal(MagicStringAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task The_canonical_attribute_still_requires_a_nonblank_reason()
    {
        string source = CanonicalAttribute + """
            [ImageGen.Domain.CodeAnalysis.AllowMagicStrings(" ")]
            class C { }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new MagicStringAnalyzer(), source));
        Assert.Equal(MagicStringAnalyzer.JustificationDiagnosticId, diagnostic.Id);
    }
}
