---
jira: DMS-1445
jira_url: https://edfi.atlassian.net/browse/DMS-1445
epic: DMS-1402
---

# Story: Compile Natural-Key Probe Metadata

## Outcome

Give every concrete resource an independently compiled, runtime-usable natural-key probe derived
from the relational model instead of from RI trigger metadata. `UX_<R>_RefKey` emission stays
conditional on inbound references (the existing `EnsureTargetUnique` rule); reference-target probes
exist exactly for the resources that carry a RefKey, and own-key probes exist for every concrete
resource because they bind `UX_<R>_NK`.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [`/deletes` on never-referenced resources](../../design-docs/natural-key-resolution.md#deletes-recreated-row-detection-on-never-referenced-resources)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1444 — document/resource invariant and abstract ResourceKeyId](02-document-resource-invariant-and-abstract-resource-key.md).
- This story blocks DMS-1446 and, together with DMS-1448, DMS-1449 and DMS-1455.

## Implementation Scope

- Compile reference-target, own-key, and shared descriptor probe metadata from the relational model.
  Reference-target probes are compiled for every resource that carries `UX_<R>_RefKey` (referenced
  resources) and for every abstract identity table; own-key probes are compiled for every concrete
  relational resource over its `UX_<R>_NK` contract.
- Do not change `UX_<R>_RefKey` emission. Do not derive probe metadata from trigger metadata,
  constraint names, discriminator parsing, or emitted SQL.
- Bind storage-resolved columns: key-unified identity parts bind the canonical storage column, not
  a generated alias; abstract probes bind the stored concrete `ResourceKeyId`.
- Retain each key entry's physical column, scalar type or descriptor-resource binding, and canonical
  identity JSON path/diagnostic name; own-key document-reference parts carry the
  `DocumentReferenceBindings` index for the reference site that supplies the resolved `..._DocumentId`.
- Add an empty-identity compile guard and an every-resource parity guard against live trigger
  derivation.

## Acceptance Criteria

- Every referenced concrete resource and every abstract identity table has a reference-target probe;
  every concrete resource has an own-key probe; a never-referenced resource has an own-key probe and
  no reference-target probe, and `MappingSet` validation rejects a reference-target probe for a
  resource without RefKey inventory.
- Golden DDL is unchanged by this story (no new `UX_<R>_RefKey` constraints).
- Runtime dictionaries contain reference-target, own-key, and shared descriptor probe contracts in
  semantic key-column order.
- Abstract probes carry the concrete `ResourceKeyId`.
- Canonical diagnostic paths survive compilation.
