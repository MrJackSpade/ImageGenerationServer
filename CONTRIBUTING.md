# Contributing

Contributions are welcome and entirely optional. Use of the project is not conditioned on
contributing. Modifying the project in order to prepare a contribution is permitted; see the
CONTRIBUTIONS clause of [LICENSE](LICENSE) for the rights you grant by submitting one. The
license is personal, non-commercial use only — do not introduce commercial or redistribution
language anywhere in the project.

This document describes the ideal state of the code, not the current state. Where existing code
deviates from a rule here, the rule wins: new code follows this document, and changes to old code
move it toward compliance, never further from it.

## Building and testing

Prerequisite: .NET 10 SDK. See [docs/BUILDING.md](docs/BUILDING.md) for the full guide.

```
dotnet build ImageGen.slnx
dotnet test tests/ImageGen.Tests                          # SQLite — no server required
IMAGEGEN_TEST_SQLSERVER=1 dotnet test tests/ImageGen.Tests  # SQL Server
```

Both database provider runs must pass. CI runs both on every pull request.

Additional checks, by what the change touches:

- **Client JS** (`src/ImageGen.Web/wwwroot/js/`): `pwsh tools/check-js-json-only.ps1`. The build
  runs this on Windows only; CI does not run it — run it yourself.
- **`configurations/`**: `python tools/gen-models-doc.py` regenerates `docs/MODELS.md`. Never
  hand-edit that file.
- **Catalogue or render behavior**: validate against a live instance —
  `tools/ui-smoke-ready.ps1` to list testable configurations, `tools/ui-smoke.ps1 -Only <ids>`
  to run them, `tools/ui-smoke-triage.ps1` to bucket the failures.

## The build is a gate

The solution ships custom Roslyn analyzers that run as errors on every project, alongside
nullable warnings as errors and code style enforced in build. A change that does not build
clean is not a candidate for review.

- Never downgrade a severity, suppress a diagnostic, or edit `.editorconfig` to admit a change.
- The escape-hatch attributes (`[AllowMagicStrings]`, `[AllowNullable]`) require a written
  justification and are for genuine one-offs, not for routing around a rule.
- Tests encode standards too (no prompt-bearing values in logs, catalogue linkage, schema
  migration shape). A red test is a defect in the change, not in the test.

## Code style

Beyond what the build enforces:

- One public type per file; the filename matches the type. Folders mirror namespaces.
- Block-scoped namespaces.
- Explicit types on the left of a declaration; target-typed `new()` and collection
  expressions `[]` on the right.
- Member order within a type: constants, fields, constructors, properties, methods — grouped
  by accessibility, alphabetical within each group.
- One blank line between every member, including between consecutive field declarations.
- Private fields `_camelCase`. Constants `SCREAMING_SNAKE_CASE`. Interfaces `I`-prefixed.
  An extension class is named after the type it extends, verbatim (`IChannelExtensions`).
- No `Async` suffix on Task-returning methods, except where mirroring an external API's name.
- No `#region`.
- Declaration comments are XML doc comments (`///`) — including on private members whose
  semantics are non-obvious. Inline comments state constraints; they never narrate the next
  line or describe the change that produced them.
- Expression bodies only for trivial one-line properties. Methods use block bodies with an
  explicit `return`.
- `this.` prefix on instance method calls; property access unqualified.
- Guard clauses first: validate inputs at the top of the method and return or throw before any
  work. A method validates its own inputs on its own contract, regardless of what callers
  already checked.
- LINQ sparingly; hot paths use explicit loops.
- Immutability by default: `readonly` fields, `{ get; init; }`, private setters. Mutable
  public models are acceptable only where a serializer requires them.
- Exceptions are for programmer error and broken invariants. Expected, user-facing failures
  return a result object or `bool` — they do not throw.
- One dedicated `private readonly` lock object per protected structure.

## Design rules

- **Fail fast.** Never swallow an error, add a fallback, substitute a default on failure, or
  silently skip an item. If a fallback seems genuinely warranted, propose it in the issue or
  pull request instead of adding it.
- **Invalid input throws.** It is never corrected, clamped, or coerced to a default. Guards go
  through `ImageGen.Domain.Ensure`, not hand-rolled `if`/`throw`.
- **No invented values.** Timeouts, retry counts, cache sizes, limits, and defaults are never
  made up to fill a gap. A bound that is genuinely required comes from configuration or from
  the maintainer — ask.
- **Prefer declarations over validation.** `required`, non-nullable types, and strict
  serializer options enforce contracts; scattered manual guards do not.
- **Model states explicitly.** `null` and `""` are different states. Defensive
  `IsNullOrWhiteSpace` guards that paper over an ambiguous model are the defect, not the fix.
- **Each JSON configuration shape is its own concrete class** with required members; a
  discriminator selects the contract. No shared class with nullable branch fields. Unknown
  members are parse errors.
- **No legacy-format fallbacks.** Migrate data once; never read an old field name "just in
  case".
- **Client JS consumes JSON.** Endpoints called from script return JSON; the client builds the
  DOM from it. Fetched HTML is never fed to the DOM (`tools/check-js-json-only.ps1` enforces
  the fingerprints).
- **No `localStorage`/`sessionStorage`.** Client state persists in per-user account settings.
- **UI is minimal.** Controls get a terse name; behavior detail goes in a tooltip, never an
  always-visible explanatory label.
- **Refactor risk away.** When a change is risky because the surrounding code is tangled,
  restructure until the change is safe, then make it. No minimal-touch hacks around fragility.
- **Migrations and backfills are standalone throwaway executables** outside the solution —
  never part of the application.

## Where things live

- [ARCHITECTURE.md](ARCHITECTURE.md) is the authority on the design; read it before any
  structural change. Dependencies point inward: ports are declared in `ImageGen.Application`,
  each adapter implements one port, adapters never reference each other, and `ImageGen.Web` is
  the only composition root.
- `configurations/workflows/<id>.json` and `configurations/models/<id>.json` — one file per
  workflow and per model file. Every file a workflow links must have a model entry; a missing
  entry hides the workflow silently (presence gating).
- The database schema files under `src/ImageGen.Infrastructure/Database/` are an append-only,
  version-segregated history. Never modify a released block; a schema change is a new version
  block, and a new column is a guarded `ALTER`.
- `comfy-patches/` and `comfy-nodes/` carry every ComfyUI divergence. A node pack without a
  `packs.json` entry never reaches ComfyUI.
- UI changes follow [docs/DESIGN_PHILOSOPHY.md](docs/DESIGN_PHILOSOPHY.md).

## Submitting

- Fork, branch, and open a pull request against `main`. CI must be green.
- Commit subjects: `type(scope): imperative summary`, lowercase, with a trailing issue
  reference when one exists — `fix(video): step the seconds control by the model's cadence (#194)`.
  Scopes are feature areas (`video`, `workflows`, `history`), not project names.
- One concern per pull request.
