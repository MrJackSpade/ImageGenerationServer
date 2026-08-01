; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------
IMGDOC001 | Documentation | Error | Declaration comments must be XML doc comments (///).
IMGDB001 | Portability | Error | Provider-typed DbDataReader getter; use DbValueExtensions instead.
IMGDB002 | Portability | Error | Unboxing cast of an ExecuteScalar result; use DbValueExtensions instead.
