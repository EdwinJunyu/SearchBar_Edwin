# Query Refactor Implementation Plan

> Execute the three authorized changes continuously; use a fresh whole-branch reviewer after implementation.

**Goal:** simplify review findings 1–3 without changing behavior.
**Architecture:** focused helper classes, one summary projection/policy, immutable query context.
**Tech Stack:** C#/.NET 10, SQLite, WPF; no new packages.
**Spec:** ../specs/2026-11-25-query-refactor-design.md

## Constraints and review focus
- Preserve old schema support, summary stamps, source identity and lazy MDX reads.
- Preserve decimal/scientific calculator behavior and original detail anchor offsets.
- File queries must survive catalog-only changes; catalog results must not.
- Cancellation, hidden windows and IME composition must not publish stale suggestions.
- Retarget all reflection callers; keep Windows-only code out of portable helpers.

## Tasks
- [x] 1. Extract summary SQL policy and named columns from DictionaryStore.Find; compare projection behavior against baseline.
- [x] 2. Extract Calculator, EntryDetailFormatter, FileHit, FileRefindHistory and EverythingSearch; update reflection tests; verify moved bodies unchanged.
- [x] 3. Introduce SearchRequestContext, share validity checks in catalog/dictionary/prefix/file queries; add context transition checks.
- [x] Independent review and available checks; record platform limitations.
- [ ] Windows build and regression suite before integration.

## Execution notes
Baseline build blocked: dotnet is not on PATH. Terminal GitHub DNS is unavailable. Reconstructed exact Git tree and signed commit through the connected provider; both object hashes match origin main. Implementation proceeds within the authorized scope; tests unavailable here must not be reported as passed.

## Verification results
- Source comparison: all five extracted implementations match the baseline after normalizing the intended access modifiers and class wrappers.
- SQLite differential check: 19,200 synthetic rows across 64 schema/query configurations; all 22 projected fields equal the baseline after mapping by aliases. Includes parser/revision/stamp presence, inline/indexed/merged bodies and phrasal queries. This checks generated SQL projections, not the C# compiler or MDX reader.
- Independent review found two stale reflection signatures in UI regression tests; both now construct `SearchRequestContext`. Follow-up review found no remaining concrete defect.
- `git diff --check` passed.
- Added 24 calculator/context assertions in `tests/RefactorChecks`. Not executed: this host has no .NET SDK or PowerShell. Full application build and Windows integration also remain unverified.

## Remaining verification commands (Windows with .NET 10 and PowerShell)
```powershell
dotnet run --project tests/RefactorChecks/RefactorChecks.csproj -c Release
dotnet build -c Release
pwsh -File regressions.ps1 -BackendTestDirectory "$env:TEMP\searchbar-backend-checks"
pwsh -File regressions.ps1 -WindowDesktop
```
The application uses Windows SQLite and Everything; the standalone helper checks need only .NET 10.
