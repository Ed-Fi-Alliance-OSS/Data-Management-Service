---
status: accepted
date: 2026-10-07
jira: DMS-1268
related:
  - DMS-1236
  - DMS-1180
  - DMS-1010
  - DMS-943
---

# Decision Record: Delete Action of the Safety-Net Foreign Keys to `dms.Document`

## Decision

Every resource root table and `dms.Descriptor` keeps its foreign key to `dms.Document`, and
that key no longer cascades. The relational model emits it with the `Restrict` delete action
(`ReferentialAction.Restrict`, manifest `on_delete: "Restrict"`), which renders as
`ON DELETE RESTRICT` on PostgreSQL and `ON DELETE NO ACTION` on SQL Server.

The write path is unchanged: DMS deletes the root row (or the `dms.Descriptor` row) first and
the `dms.Document` row second, inside one transaction, so the key never has anything to
cascade. The key only guards direct database modification. A `DELETE FROM dms.Document` issued
while the root row still exists now fails (SQLSTATE `23503`, or `23001` on PostgreSQL 18;
error `547` on SQL Server) instead of silently removing the root row without a `/deletes`
tombstone.

The cascade-maintained tables keep `ON DELETE CASCADE`: `dms.DocumentCache`,
`dms.DocumentProjectionWork`, `dms.ReferentialIdentity`, the abstract identity tables, and the
child collection and `_ext` tables. Their rows are removed only by the cascade, so the
cascade is their deletion path, not a safety net.

Physical objects are owned by [data-model.md](../data-model.md). The delete ordering contract
and its trigger-side rationale are owned by
[change-queries.md](../change-queries.md#cascade-ordering-requirement-for-deletes) and
[transactions-and-concurrency.md](../transactions-and-concurrency.md).

## Context

DS 5.2 has about 140 resource root tables, each with a foreign key to `dms.Document`. Because
the application already deletes the root row first, those keys were a pure safety net, but
the database still had to evaluate them on every `dms.Document` delete:

- **PostgreSQL** dispatches one RI trigger per foreign key per deleted row regardless of the
  delete action (139 dispatches, about 1.8 ms per `dms.Document` delete on the DS 5.2
  database). The action decides how many queries each trigger runs. `CASCADE` and `RESTRICT`
  run one probe of the referencing table. `NO ACTION` runs two, because its end-of-statement
  semantics require a "does another parent row with this key exist now?" re-check before the
  probe, which `pg_stat_statements` confirmed as 278 nested statements per delete (140.6M
  primary-key re-checks over the `NO ACTION` run against 6M for the others). With the keys
  dropped the same delete cost 0.066 ms.
- **SQL Server** compiles referential actions into the DELETE plan. With `CASCADE` the
  `DELETE FROM dms.Document` plan referenced 472 tables through 472 clustered index delete
  operators and walked all of them on every delete (331 ms per execution). With `NO ACTION`
  the same plan has 139 index seeks and nothing else (1.47 ms).

The volume (write) performance test, where roughly four of every ten requests are DELETEs,
measured the following. The "FKs dropped" rows are the reference point, not an option that
was shipped.

SQL Server:

| FK state | req/s | vs dropped | mean / p50 / p95 / p99 ms | DB CPU core-s per 1k req |
| --- | --- | --- | --- | --- |
| FKs dropped (reference) | 1,037 | | 8.71 / 7 / 16 / 33 | 3.25 |
| `ON DELETE CASCADE` (shipped before this decision) | 63.6 | -93.9% | 156 / 30 / 500 / 680 | 87.5 |
| **`ON DELETE NO ACTION` (decision)** | 992 | -4.4% | 9.22 / 8 / 17 / 33 | 3.61 (+11%) |

PostgreSQL:

| FK state | req/s | vs dropped | mean / p50 / p95 / p99 ms | DB CPU core-s per 1k req |
| --- | --- | --- | --- | --- |
| FKs dropped (reference) | 1,585 | | 5.42 / 5 / 9 / 12 | 2.15 |
| `ON DELETE CASCADE` (shipped before this decision) | 1,351 | -14.7% | 6.54 / 6 / 10 / 19 | 3.15 (+47%) |
| **`ON DELETE RESTRICT` (decision)** | 1,377 | -13.1% | 6.38 / 6 / 10 / 14 | 3.06 (+42%) |
| `ON DELETE NO ACTION` | 1,283 | -19.0% | 6.91 / 7 / 10 / 13 | 3.66 (+70%) |

The whole cost sits on the DELETE endpoints in every run (PostgreSQL DELETE mean: 4.3 ms
dropped, 6.4 `RESTRICT`, 6.7 `CASCADE`, 7.8 `NO ACTION`; POST and PUT unchanged within
0.3 ms). `RESTRICT` versus `CASCADE` on PostgreSQL is within single-run noise; `RESTRICT` is
chosen because it is never worse and makes the single-probe behaviour explicit. The earlier
figures that opened this question (1.85 ms per delete, 51.5% of DB execution time, 849 vs
840 req/s at 20 users) came from the July PostgreSQL campaign on an older build and a 4-vCPU
web host; the tables above supersede them. Raw results are attached to DMS-1268 and the
Northridge comparison story DMS-1236.

## Options Assessed

Each option was assessed against integrity under direct database modification, dialect
coverage, implementation and migration cost, and operational implications.

| Option | Integrity under direct DB modification | Dialect coverage | Implementation / migration cost | Operational implications | Disposition |
| --- | --- | --- | --- | --- | --- |
| **A. Keep the `CASCADE` keys as-is** and re-evaluate when DB CPU is the binding constraint | Full, but a direct `DELETE FROM dms.Document` silently removes the root row with no tombstone | Both | None | None, but SQL Server pays the 472-table cascade plan on every delete (63.6 req/s, 87.5 core-s per 1k requests) | Rejected: the SQL Server cost is not acceptable at any load |
| **B. Asynchronous `dms.Document` purge**: root rows and identity bookkeeping cleaned synchronously, the `dms.Document` row tombstoned and bulk-purged by a maintenance job | Full; keys unchanged | Both | High: a tombstone marker, filters on every read and uniqueness path so a tombstoned document is invisible, a purge job and its tests; identity rows must stay synchronously cleaned or identity re-use returns spurious duplicate-identity 409s | A scheduled purge job per deployment, monitoring of purge lag, and a larger `dms.Document` table between runs | Rejected: moves the cost rather than removing it, and adds a job to operate |
| **C. `session_replication_role = replica`** scoped to the application's delete transaction | Full for every other session; the application's own ordered delete skips RI triggers | PostgreSQL only; no SQL Server analog | Medium: `ENABLE ALWAYS` on tracked-changes and stamp triggers (replica role disables ordinary triggers), `GRANT SET ON PARAMETER` (PostgreSQL 15+) | A superuser-adjacent privilege for the application role and a trigger enable-mode that must survive every DDL regeneration | Rejected: PostgreSQL-only, with no SQL Server equivalent; kept for documentation |
| **D. Partition `dms.Document` by resource** with per-partition keys | Full | PostgreSQL only; SQL Server native partitions are not referenceable, and the partitioned-view emulation splits the Document hub (`ReferentialIdentity`, uuid lookup) | High: invasive PK and unique-index changes across the hub | Partition maintenance when resources are added | Rejected: PostgreSQL-only, with no SQL Server equivalent; kept for documentation |
| **E. Drop the keys and emit compensating targeted RI**: one delete trigger on `dms.Document` dispatching by `ResourceKey` to the single root table, plus per-root insert checks | Partial: hand-rolled RI must replicate FK locking (`FOR KEY SHARE`) to avoid concurrency races | Both, with two trigger implementations | High: the trigger bodies, their locking, and their tests on both engines | Trigger logic to keep in step with the model on every DDL regeneration | Rejected: replaces a declarative guarantee with code that must be proven race-free |
| **F. Allow-list key drop** (the original proposal, optionally behind an opt-in "enforced safety-net FKs" mode) | None for the dropped keys: a direct `DELETE FROM dms.Document` leaves orphaned root rows | Both | Low, but must never touch the cascade-maintained tables | A mode switch to document and support; two schema shapes to test | Rejected: the largest saving, but it accepts the direct-modification risk the team decided not to take |
| **Chosen: keep the keys, change the delete action to `RESTRICT` / `NO ACTION`** | Full, and stronger than before: a direct `DELETE FROM dms.Document` with a live root row is rejected instead of silently cascading | Both, with one logical action rendered per dialect | Low: one enum value, one dialect rendering, regenerated fixtures | None: no job, no privilege grant, no trigger enable-mode, no partitioning | **Accepted** |

## Rationale

The constraints are kept because they protect integrity under direct database modification
(scripts, migrations, manual fixes), which the team treats as a conscious requirement rather
than a side effect to trade away in a performance ticket. Changing the delete action removes
the part of the cost that was pure waste: on SQL Server the 472-table cascade plan, and on
PostgreSQL the `NO ACTION` re-check. What remains is one probe per root table per delete.

The residual cost against the FK-dropped reference is accepted: about 4% throughput and 11%
DB CPU on SQL Server, about 13% throughput and 42% DB CPU on PostgreSQL. The hypothesis is
that real workloads do not look like the volume test, which deletes roughly four of every ten
requests because it creates and immediately removes its own documents. Against that, the
chosen option needs no purge job, no hand-rolled RI, no replica-role privilege and no
partitioning, and it works identically on both dialects.

`RESTRICT` is chosen over `NO ACTION` on PostgreSQL because it is the single-probe check: for
the DMS write path, which always deletes the root row first, the two are semantically
identical, and `RESTRICT` skips the parent re-check. SQL Server has no `RESTRICT` keyword and
its `NO ACTION` is already an immediate single probe compiled into the plan, so the logical
`Restrict` action renders as `NO ACTION` there.

## Consequences

- Scripts that modify the database directly must delete the root row (or the `dms.Descriptor`
  row) before the `dms.Document` row. The reverse order now fails instead of silently removing
  the root row without a tombstone.
- Only a newly provisioned database gets the new keys. Both dialects add a foreign key only
  when no key with that name exists, and generated DDL is not an input to the effective schema
  hash, so a database provisioned by an earlier 8.1 build keeps its `CASCADE` keys and passes
  startup validation. On PostgreSQL the delete action is part of the hash that shortens long
  constraint names, so five DS 5.2 key names change and a rerun against an older database
  would add those `RESTRICT` keys beside the old `CASCADE` ones. The remedy is to provision a
  fresh database; `SchemaHashConstants.RelationalMappingVersion` is not bumped for this
  change, because the 8.1 line has already consumed its bump.
- The relational model gains `ReferentialAction.Restrict`. Dialects render it; the manifest
  records it as `on_delete: "Restrict"`.
- `dms.Descriptor` follows the resource-root rule, so test fixtures that clear the `dms.*`
  tables directly must delete `dms.Descriptor` before `dms.Document`.
- Integration tests on both engines pin the delete action of every root and `dms.Descriptor`
  key, the kept cascades on the cascade-maintained tables, the rejection of a direct
  `dms.Document` delete with a live root row, and the success of the ordered delete.

## Condition to Reopen

Reopen this decision if a deployment is DB-CPU-bound and delete-heavy, for example bulk
re-syncs or year-end purges, where the one-probe-per-root-table cost of each `dms.Document`
delete is the binding constraint. The measurements above give the ceiling: dropping the keys
would recover at most about 4% throughput on SQL Server and 13% on PostgreSQL in the
delete-heavy volume mix. Option F (an opt-in allow-list drop) is the cheapest path if that
ceiling is ever worth the integrity trade-off; option B (asynchronous purge) is the path that
keeps the keys.
