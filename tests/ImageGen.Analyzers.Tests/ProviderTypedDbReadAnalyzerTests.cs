using Microsoft.CodeAnalysis;

namespace ImageGen.Analyzers.Tests;

public sealed class ProviderTypedDbReadAnalyzerTests
{
    [Theory]
    [InlineData("int")]
    [InlineData("Int32")]
    [InlineData("IntAlias")]
    [InlineData("System.Int32")]
    [InlineData("short")]
    [InlineData("float")]
    [InlineData("decimal")]
    [InlineData("System.DateTime")]
    public async Task ExecuteScalar_casts_are_matched_by_semantic_target_type(string target)
    {
        string source = $$"""
            using System;
            using System.Data.Common;
            using IntAlias = System.Int32;
            class C
            {
                static object Read(DbCommand command) => ({{target}})command.ExecuteScalar()!;
            }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new ProviderTypedDbReadAnalyzer(), source));
        Assert.Equal(ProviderTypedDbReadAnalyzer.ScalarDiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task An_unrelated_ExecuteScalar_name_is_not_reported()
    {
        const string source = """
            class FakeCommand { public object ExecuteScalar() => 1; }
            class C { static int Read(FakeCommand command) => (int)command.ExecuteScalar(); }
            """;

        Assert.Empty(await AnalyzerTestHarness.AnalyzeAsync(new ProviderTypedDbReadAnalyzer(), source));
    }

    [Theory]
    [InlineData("IDataRecord", "GetByte")]
    [InlineData("IDataReader", "GetBoolean")]
    [InlineData("IDataRecord", "GetDecimal")]
    [InlineData("IDataReader", "GetDouble")]
    [InlineData("IDataRecord", "GetFloat")]
    [InlineData("IDataReader", "GetInt16")]
    [InlineData("IDataRecord", "GetInt32")]
    public async Task Provider_sensitive_numeric_getters_are_reported_through_reader_interfaces(
        string readerType,
        string getter)
    {
        string source = $$"""
            using System.Data;
            class C { static object Read({{readerType}} reader) => reader.{{getter}}(0); }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new ProviderTypedDbReadAnalyzer(), source));
        Assert.Equal(ProviderTypedDbReadAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("bool")]
    [InlineData("decimal")]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("short")]
    [InlineData("int")]
    public async Task Numeric_GetFieldValue_pairs_are_reported(string type)
    {
        string source = $$"""
            using System.Data.Common;
            class C { static object Read(DbDataReader reader) => reader.GetFieldValue<{{type}}>(0); }
            """;

        Diagnostic diagnostic = Assert.Single(await AnalyzerTestHarness.AnalyzeAsync(new ProviderTypedDbReadAnalyzer(), source));
        Assert.Equal(ProviderTypedDbReadAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Theory]
    [InlineData("byte[]")]
    [InlineData("long")]
    [InlineData("string")]
    public async Task Cross_provider_safe_GetFieldValue_pairs_are_not_blanket_banned(string type)
    {
        string source = $$"""
            using System.Data.Common;
            class C { static object Read(DbDataReader reader) => reader.GetFieldValue<{{type}}>(0); }
            """;

        Assert.Empty(await AnalyzerTestHarness.AnalyzeAsync(new ProviderTypedDbReadAnalyzer(), source));
    }

    [Fact]
    public async Task GetGuid_is_not_banned_without_a_portable_replacement_contract()
    {
        const string source = """
            using System.Data;
            class C { static object Read(IDataRecord reader) => reader.GetGuid(0); }
            """;

        Assert.Empty(await AnalyzerTestHarness.AnalyzeAsync(new ProviderTypedDbReadAnalyzer(), source));
    }

    [Fact]
    public async Task An_unrelated_numeric_getter_name_is_not_reported()
    {
        const string source = """
            class Record { public short GetInt16(int ordinal) => 1; }
            class C { static short Read(Record record) => record.GetInt16(0); }
            """;

        Assert.Empty(await AnalyzerTestHarness.AnalyzeAsync(new ProviderTypedDbReadAnalyzer(), source));
    }
}
