# Query and window refactor

Scope: implement review findings 1, 2, and 3 while preserving search results, legacy read-only dictionaries, cancellation behavior, UI text and keyboard behavior. No new dependencies or schema migration.

Use a small summary policy with named SQL projections and named result access; retain SQL-side filtering and lazy source-body reads. Extract pure calculator/entry formatting and independent file-search services into real classes, not window partial files. Capture query revision, catalog revision and cancellation in an immutable context with explicit visibility/composition checks at each publication boundary.

Alternatives: a full MVVM/repository rewrite would increase scope and risk; splitting only partial classes would retain coupling. Choose focused extraction and shared request predicates. Keep independent generation/audit implementations unchanged.

Verification: preserve existing regressions and retarget reflection to extracted classes; add portable calculator/request-context checks; compare moved methods and SQL projections with the original snapshot. Windows WPF/Everything remains a required integration gate before merge.
