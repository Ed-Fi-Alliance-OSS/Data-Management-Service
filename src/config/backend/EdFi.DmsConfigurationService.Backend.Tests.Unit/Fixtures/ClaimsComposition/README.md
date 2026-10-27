# Fragment composition characterization

`base.json` is the complete input document. Each scenario's `fragments/` directory
is staged recursively, preserving filenames and relative paths. `expected.json`
is a literal complete document, authored independently of the composer. Empty
and missing scenarios intentionally have no fragment files. These fixtures can
be linked as content by CLI tests without a shared test-helper assembly.

- `first-wins` / `reverse-wins`: swapping application order changes the exact
  winning actions and strategies. Names differing only in case share registration
  and grant precedence. Exact `edfi/students` wins over singular `edfi/student`;
  `edfi/categories` falls back to `edfi/category`.
- `base-wins`: existing grants and the non-reserved catalog entry survive.
- `invalid-registration`: whitespace prevents catalog registration, but applied
  grants remain and CMS continues.
- `empty-registration`: composition continues, then CMS schema validation fails
  at the empty grant name. The expected document is the composed invalid document.
- `unmatched`: an unmatched grant is skipped while the other grant and registration
  survive.
- `discarded-children`: a matched claim with no child collection discards children;
  an existing collection accepts only children with nonempty names. T05 marks
  these discards incomplete without changing this scenario's CMS result.
- `malformed`: a valid fragment followed by malformed JSON fails composition;
  the expected document is the CMS base-only fallback.
- `retired`: retired filenames, unrelated files, and Claims.json are ignored.
- `no-resource-claims`: null resource claims are skipped.
- `null-structures`: missing/null grant names and null resource claims, children,
  applied claim sets, actions, and strategies produce contextual fatal failures.
  CMS still rejects loading; CLI failures preserve the destination without a stack trace.
- `empty` / `missing`: the composer returns the base in either case. CMS itself
  rejects a missing configured directory before composition.

Assertions compare complete catalogs and hierarchies, including array order,
through the shared composer, CMS adapter, and real provider/schema validator.

## Unapplied input tables (T05, reusable by T10)

`unapplied-input/*-cases.json` are literal scenario tables. Each row supplies a
complete `fragment` to write as `input-claims.json`, an `expected` document path,
and ordered `diagnostics` (code, claim, claim set, indexed JSON path). Paths are
relative to `unapplied-input/`. The optional `base` path replaces `../base.json`.
The test adds only the temporary fragment source path to expected diagnostics.
Expected documents were authored independently of the composer; do not regenerate
them from its output. CLI tests can stage the same rows without a test-helper
assembly. These are schema-valid CMS results even when composition is incomplete.

- `policy-field-cases.json`: matched parents, new parents, non-parent entries,
  and copied children under both parent paths. Each ignored field has nonempty,
  empty, absent, and mixed-case binding cases. Matched parent policies include
  identical and differing values: both are discarded. New-parent policies that
  actually apply have a separate exact expected document. Diagnostic paths use
  canonical JSON spelling, including `_defaultAuthorizationStrategiesForCrud`.
- `discarded-child-cases.json`: a matched leaf without a child collection;
  missing/empty child names alongside a successfully appended child; accepted
  name-only children; a URI claim matched by a differently cased lookup name.
  Discard diagnostics retain the matched hierarchy claim and original indexes.
- `null-policy-cases.json`: null-only, mixed valid/null, empty and absent lists
  at both `OfType` discard points. Mixed nulls occupy indexes 0 and 2; remaining
  policies retain their order and exact grants/defaults. Null values in ignored
  policy fields still produce unapplied-input diagnostics without failing CMS loading.
- `informational-cases.json`: isolated `Isolated` claim set, enabled/disabled/
  absent actions crossed with absent/empty/applied overrides. The declared action
  is Delete and the override action is Read, proving only the override controls
  the grant. Export IDs and descriptive flags are included and remain informational.

The parameterized fixture asserts complete shared output, real CMS provider output
and schema validation, exact diagnostics/completeness, and unchanged base input
separately. T10 should reuse these inputs for strict CLI rejection/acceptance and
resolution assertions.
