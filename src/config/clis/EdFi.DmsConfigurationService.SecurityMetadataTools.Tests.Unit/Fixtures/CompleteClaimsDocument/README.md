# Complete-document validation fixtures

T08 inputs for the CLI-only validator; reuse these files in merge/resolve/verify command tests.
All documents listed in `failures.json` pass the shared schema. The manifest records the
exact integrity failure kind, original value, and locations (first occurrence, then duplicate).
Policy agreement does not exempt duplicate hierarchy names. Non-leaf names and separate
branches share the same ordinal uniqueness scope.

`valid.json` retains catalog order/spelling, an empty claim set, a case-only hierarchy
reference, and inherited Read/NamespaceBased permission. `distinct-uri-text.json` preserves
case, host spelling, default ports, escapes, and trailing slashes as distinct ordinal names.
Length/whitespace boundary and rejected input-shape cases are parameterized in the tests.
`catalog-final-newline.json` covers acceptance of a final newline through the validator,
preserving the existing CMS naming regex's `$` anchor behavior without normalizing the name.
