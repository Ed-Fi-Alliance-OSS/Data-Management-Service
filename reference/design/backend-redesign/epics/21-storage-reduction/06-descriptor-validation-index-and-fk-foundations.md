---
jira: DMS-1448
jira_url: https://edfi.atlassian.net/browse/DMS-1448
epic: DMS-1402
---

# Story: Add Descriptor Validation, Index, and Foreign-Key Foundations

## Outcome

Prepare the final provider-specific descriptor storage and equality contract while keeping the legacy
RI resolver fully functional until the atomic resolver cutover.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1443 — SQL Server identity collation contract](01-sql-server-identity-collation-contract.md).
- Depends on [DMS-1444 — document/resource invariant and abstract ResourceKeyId](02-document-resource-invariant-and-abstract-resource-key.md).
- Depends on [DMS-1447 — PostgreSQL floor and descriptor-collation upgrade](05-postgresql-17-and-descriptor-collation-upgrade.md).
- Together with DMS-1445, this story blocks DMS-1449 and DMS-1455.

## Implementation Scope

- Add a shared well-formed-without-NUL validator (validate-and-assert) and reject NUL in descriptor
  writes and references with a path-attributed 400. NUL is the only malformed input that can reach a
  C# string (`\u0000` in a body, `%00` in a query string).
- Reject unpaired-surrogate JSON escapes (`\uD800`-class) at body parse: `ParseBodyMiddleware` must
  materialize string leaves after `JsonNode.Parse` and translate the resulting
  `InvalidOperationException` ("Cannot read incomplete UTF-16 JSON text…") into the existing
  malformed-body 400 with the JSON path. This is body-wide (any resource, any string property) by
  necessity — STJ throws at first read, so it cannot be descriptor-specific — and replaces today's
  unmapped 5xx. Query strings cannot carry an unpaired surrogate; no query-side check is added.
- Retain existing `ToLowerInvariant()` and UUIDv5 normalization wherever the active RI path requires
  them. Raw descriptor identities begin at DMS-1451.
- On PostgreSQL, emit a unique expression index on
  `lower("Uri" COLLATE "pg_c_utf8"), "ResourceKeyId"` without adding a column.
- On SQL Server, emit non-persisted `UriLowered AS LOWER([Uri])` and a unique index on
  `(UriLowered, ResourceKeyId)`.
- Remove `FK_Descriptor_Document` and `FK_Descriptor_ResourceKey`, replacing them with the single
  `FK_Descriptor_DocumentResourceKey` foreign key on `(DocumentId, ResourceKeyId)`.
- Retire the `ResourceKeyId` equality guard at the top of `TF_/TR_Descriptor_Stamp_Document`
  (both dialects: the `IF NOT EXISTS (… dms.Document WHERE DocumentId = NEW.DocumentId AND
  ResourceKeyId = NEW.ResourceKeyId) THEN RAISE/THROW` block in `CoreDdlEmitter`) and the emitter
  comment that justifies it by "no FK ties the two together"; the composite FK is the sole owner of
  that invariant. Keep the triggers' no-op guard and stamp/mirror behavior unchanged.
- Retain discriminator-authoritative uniqueness through the transition.

## Acceptance Criteria

- Golden DDL contains exactly the required provider index shape, SQL Server computed column, and
  composite foreign key, and no `ResourceKeyId` equality guard in either descriptor stamping
  trigger; the `CoreDdlEmitterTests` pin flips from asserting the guard to asserting its absence.
- Derived models, manifests, and generated DDL contain neither of the exact legacy constraint names
  `FK_Descriptor_Document` and `FK_Descriptor_ResourceKey` after the composite replacement.
- Write/reference validation identifies the concrete descriptor path or `namespace`/`codeValue`
  field for NUL.
- A JSON `\uD800`-class escape in any body string property returns a malformed-body 400 (never a
  5xx) on a descriptor and on a non-descriptor resource (query-side pins belong to DMS-1451, which
  owns descriptor query preprocessing).
- The PostgreSQL golden DDL pins `lower("Uri" COLLATE "pg_c_utf8")` in the expression index, and a
  live fixture on a database created with a non-default collation (`LC_COLLATE='C'` or an ICU
  locale) proves the index folds identically to the default-collation database.
- Corruption tests reject descriptor/document `ResourceKeyId` drift as a foreign-key violation
  (SQLSTATE 23503 on PostgreSQL, error 547 on SQL Server) on descriptor INSERT and UPDATE with a
  mismatched `ResourceKeyId`, and prove the stamping triggers still stamp/no-op correctly without
  the guard.
- Legacy RI-based descriptor resolution remains green until DMS-1451.
