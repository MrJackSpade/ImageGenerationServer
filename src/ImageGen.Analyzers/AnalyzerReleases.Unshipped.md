; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------
IMGDOC001 | Documentation | Error | Declaration comments must be XML doc comments (///).
IMGDB001 | Portability | Error | Provider-typed DbDataReader getter; use DbValueExtensions instead.
IMGDB002 | Portability | Error | Unboxing cast of an ExecuteScalar result; use DbValueExtensions instead.
IMGNULL001 | Nullability | Error | Null-forgiving operator (!) is banned; restructure so nullability is provable.
IMGNULL002 | Nullability | Error | Null-coalescing on a non-null operand; the fallback is dead code.
IMGNULL003 | Nullability | Error | Nullable value-type property/field (int?/double?/bool?/DateTime?/enum?/…); make it non-nullable with a default, or annotate [AllowNullable("reason")] where null carries a meaning no default can express.
IMGNULL004 | Nullability | Error | [AllowNullable] justification is empty; give a non-empty reason.
IMGSTR001 | Maintainability | Error | Magic string literal in an equality comparison or method/constructor/indexer argument; well-known args (Exception/ILogger message, any sql, AddParam name, ToString/ParseExact format) and [AllowMagicStrings] scopes/parameters are exempt; use a named constant.
IMGSTR002 | Maintainability | Error | [AllowMagicStrings] justification is empty; give a non-empty reason.
