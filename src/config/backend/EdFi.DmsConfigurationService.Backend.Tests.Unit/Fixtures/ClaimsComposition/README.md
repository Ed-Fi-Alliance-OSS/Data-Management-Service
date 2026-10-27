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
  an existing collection accepts only children with nonempty names.
- `malformed`: a valid fragment followed by malformed JSON fails composition;
  the expected document is the CMS base-only fallback.
- `retired`: retired filenames, unrelated files, and Claims.json are ignored.
- `no-resource-claims`: null resource claims are skipped.
- `empty` / `missing`: the composer returns the base in either case. CMS itself
  rejects a missing configured directory before composition.

Assertions compare complete catalogs and hierarchies, including array order,
through the shared composer, CMS adapter, and real provider/schema validator.
