---
jira: DMS-924
jira_url: https://edfi.atlassian.net/browse/DMS-924
---

# Story: Deterministic Canonical JSON Serialization

## Description

Create a canonical JSON serializer used by `EffectiveSchemaHash` computation (and any future mapping-pack determinism checks). Canonicalization must be byte-for-byte stable across:

- JSON property ordering
- whitespace/indentation
- platform line endings

Arrays remain in-order; objects are recursively property-sorted using ordinal string comparison.

## Acceptance Criteria

- Canonical output is stable UTF-8 bytes (no BOM), with no insignificant whitespace (minified form).
- JSON objects are recursively sorted by property name using `StringComparer.Ordinal` semantics.
- JSON arrays preserve element order exactly.
- Canonical output is identical for semantically identical JSON regardless of input formatting/ordering.
- A shared canonicalizer implementation is used by all producer/consumer code paths (generator, harness, optional pack builder).

## Tasks

1. Implement a JSON token canonicalizer that:
   1. parses to a token tree,
   2. re-emits canonical bytes with sorted object properties and preserved arrays.
2. Define and document any normalization rules required by hashing (e.g., lowercase hex output elsewhere; canonical JSON itself is structural only).
3. Add unit tests covering:
   1. object property order permutations,
   2. whitespace/line-ending differences,
   3. nested object/array cases.
