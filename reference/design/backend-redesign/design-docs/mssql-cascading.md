# SQL Server Identity-Update Cascade Handling and Foreign-Key Pruning

## Status

Design note produced by the DMS-1129 spike ("Design foreign key pruning"). It defines the
target strategy for SQL Server identity-update propagation and **supersedes** the earlier
"every SQL Server reference composite FK uses `ON UPDATE NO ACTION` plus a
`MssqlIdentityPropagationTrigger`" rule described in `overview.md`, `strengths-risks.md`,
`transactions-and-concurrency.md`, `key-unification.md`, `data-model.md`, and
`ddl-generation.md`. Those documents now point here for the SQL Server cascade rules.

The SQL Server behaviors this design depends on were confirmed empirically against
`mcr.microsoft.com/mssql/server:2022-latest` (SQL Server 2022, RTM-CU25); see
[SQL Server behavior (empirically confirmed)](#sql-server-behavior-empirically-confirmed).

Implementation is tracked separately (see [Follow-up work](#follow-up-work)); this note is the
design only.

## The problem

When a resource's identifying values change, the new values must reach every row that stores a
copy of them — the propagated identity-part columns on each direct referrer's storage table.
The two supported engines diverge:

- **PostgreSQL** allows a foreign-key graph with "cycles or multiple cascade paths", so DMS uses
  composite FKs `(…identity parts…, DocumentId)` with `ON UPDATE CASCADE` on every eligible edge
  (abstract targets, and concrete targets with `allowIdentityUpdates = true`). The engine
  propagates identity changes natively. This remains correct and unchanged.

- **SQL Server** rejects the same graph at DDL time whenever a table would be reachable by more
  than one cascade path (error **1785**). To get a schema that even *creates*, DMS previously
  stripped every SQL Server reference composite FK down to `ON UPDATE NO ACTION` and propagated
  identity changes with AFTER-style `MssqlIdentityPropagationTrigger` triggers.

That trigger-only strategy then hit a second wall. SQL Server enforces a `NO ACTION` FK check as
part of the UPDATE statement — **before** any AFTER trigger on the same table runs — and its FK
checks cannot be deferred to end-of-transaction. So a composite FK that still contains the
identity columns will reject a parent identity update for any already-referenced row (error
**547**) before the propagation trigger can fix the children. DMS-1002 worked around *that* by
removing the identity columns from SQL Server propagation-managed FKs entirely and keeping only
`…_DocumentId` (see `ReferenceConstraintPass`). The cost: SQL Server no longer enforces
referential integrity on the identity *values*. A concurrent identity update racing an insert
that references the old identity can leave a referrer holding stale identity values, and nothing
in the database rejects it.

**DMS-1129 asks whether DMS should instead adopt ODS-style FK pruning** — keep the composite FK
(identity parts included, restoring value-level RI) and remove `ON UPDATE CASCADE` only from the
*redundant* edges — and, critically, how to do so without ODS's silent-mis-prune failure mode.

## SQL Server behavior (empirically confirmed)

Six probes were run against a throwaway SQL Server 2022 container. Each result is the load-bearing
fact for one part of the design. The minimal reproduction DDL is inlined so this note is
self-contained.

| # | Probe | Result | Design consequence |
|---|-------|--------|--------------------|
| 1 | Two `ON UPDATE CASCADE` paths reach one table (diamond) | **Msg 1785** at `CREATE` | SQL Server forbids multiple cascade paths to a table — pruning is *required* on SQL Server. |
| 2 | Same diamond, redundant edge set to `ON UPDATE NO ACTION` | DDL succeeds | Converting a redundant cascade edge to `NO ACTION` (pruning) makes the graph legal. |
| 3 | `NO ACTION` composite FK that **includes identity columns**; update parent identity of a referenced row | **Msg 547**, and an AFTER UPDATE trigger that fixes the referrer does **not** rescue it | A `NO ACTION` composite FK blocks the update before the trigger runs. You cannot keep identity columns in a `NO ACTION` FK *and* rely on a trigger. |
| 4 | Kept `ON UPDATE CASCADE` composite FK (identity cols + DocumentId); update parent identity | Succeeds; referrer auto-updated | A kept cascade edge preserves full value-level RI and propagates natively. |
| 5 | `INSTEAD OF UPDATE` trigger on a table that has a cascading FK | **Msg 2113** | The "reorder children-first via `INSTEAD OF`" alternative is unavailable on any table participating in a kept cascade. |
| 6 | Diamond where the pruned `NO ACTION` edge shares a **key-unified** column with a surviving cascade path; update the shared key | Succeeds; shared column propagated | Pruning is *safe* when the pruned edge's stored column is maintained by a surviving cascade (or is immutable), because `NO ACTION` never sees an inconsistent value. |

Minimal reproductions:

```sql
-- Probe 1: multiple cascade paths rejected (Msg 1785)
CREATE TABLE dbo.A (Id int NOT NULL PRIMARY KEY);
CREATE TABLE dbo.B (Id int NOT NULL PRIMARY KEY, A_Id int NOT NULL,
    CONSTRAINT FK_B_A FOREIGN KEY (A_Id) REFERENCES dbo.A(Id) ON UPDATE CASCADE);
CREATE TABLE dbo.C (Id int NOT NULL PRIMARY KEY, A_Id int NOT NULL, B_Id int NOT NULL,
    CONSTRAINT FK_C_B FOREIGN KEY (B_Id) REFERENCES dbo.B(Id) ON UPDATE CASCADE,
    CONSTRAINT FK_C_A FOREIGN KEY (A_Id) REFERENCES dbo.A(Id) ON UPDATE CASCADE); -- 1785 here

-- Probe 3: NO ACTION composite FK incl. identity columns blocks the parent update (Msg 547),
-- and an AFTER trigger cannot rescue it because the FK check precedes the trigger.
CREATE TABLE dbo.Target (DocumentId int NOT NULL PRIMARY KEY, IdVal nvarchar(50) NOT NULL,
    CONSTRAINT UQ_Target_RefKey UNIQUE (IdVal, DocumentId));
CREATE TABLE dbo.Referrer (DocumentId int NOT NULL PRIMARY KEY,
    Target_DocumentId int NOT NULL, Target_IdVal nvarchar(50) NOT NULL,
    CONSTRAINT FK_Referrer_Target FOREIGN KEY (Target_IdVal, Target_DocumentId)
        REFERENCES dbo.Target (IdVal, DocumentId) ON UPDATE NO ACTION);
INSERT dbo.Target VALUES (1, 'old'); INSERT dbo.Referrer VALUES (10, 1, 'old');
UPDATE dbo.Target SET IdVal = 'new' WHERE DocumentId = 1; -- 547, even with an AFTER trigger present
```

### Validation on a populated database

The mechanics above were re-confirmed against a real, populated Ed-Fi ODS/API database
(`EdFi_Ods_Populated_Template`, SQL Server 2022) — the reference implementation this design
mirrors — using transactions that were rolled back, so the database was left untouched:

- **Cascade at scale on real composite keys.** Renaming one `edfi.Session` row (the 3-part natural
  key `SchoolId, SchoolYear, SessionName`) cascaded transitively — `Session → CourseOffering → Section` — rewriting 237 CourseOfferings and 237 Sections (plus their own cascade descendants)
  from a single `UPDATE`, in ~1.2 s. This is a concrete, real-data confirmation of the
  identity-update *fan-out* risk in [strengths-risks.md](strengths-risks.md): one identity change on
  a hub row synchronously rewrites hundreds-to-thousands of rows.
- **1785 on a real hub.** Adding a second `ON UPDATE CASCADE` path into `edfi.Session` (a diamond)
  failed with the verbatim `Msg 1785 … may cause cycles or multiple cascade paths`; pruning that one
  redundant edge to `ON UPDATE NO ACTION` made the identical schema legal.
- **Base-model observation.** In the stock Ed-Fi data model the cascade cluster (Section, Session,
  CourseOffering, ClassPeriod, …; 41 `CASCADE` FKs vs 1628 `NO ACTION`) is already an acyclic graph
  with no convergent diamond, so ODS pruned nothing in that schema. Pruning is exercised by specific
  key-unification topologies (the `KeyUnifiedResource`-style extension in DMS-1129), not the base
  model — so the safe-vs-unsafe classification and fail-fast matter chiefly for extensions and
  heavily key-unified resources.

## Design: pruning with a safety classification

The strategy is **hybrid, deterministic, and fail-fast**. On SQL Server, DMS re-introduces
`ON UPDATE CASCADE` (with the full composite FK, identity columns restored) on the *surviving*
edges, prunes the redundant edges to `NO ACTION`, and refuses to emit DDL for any graph where no
safe pruning exists. This replaces the current "strip identity columns everywhere + trigger"
default.

### 1. Build the cascade-eligible edge graph

Vertices are storage tables (concrete resource roots, child/collection and `_ext` binding
tables, and abstract identity tables). A directed **cascade-eligible edge** runs from a referrer
binding table to a referenced target when the reference propagates identity — i.e. the target is
abstract or the concrete target has `TransitivelyAllowIdentityUpdates = true`. This is the same
set of edges `ReferenceConstraintPass` and `DeriveTriggerInventoryPass.BuildReverseReferenceIndex`
already enumerate; the classification is a new pass over that graph, not a new graph.

### 2. Detect tables with multiple incoming cascade paths

For each target table, collect its incoming cascade-eligible edges. A table with more than one
incoming edge is a **pruning candidate vertex** — exactly the situation SQL Server rejects
(probe 1). Ordering for determinism follows the existing convention (by source table identifier,
then constraint name), mirroring ODS's `sortBy(odsTableId)` so pruning is reproducible.

### 3. Classify each edge by *coverage*, not by sort order alone

This is where DMS diverges from ODS. Before choosing which edges to prune, each candidate edge is
classified by whether pruning it is *safe*:

- **Covered / redundant** — the edge's stored identity-part columns are the same (under key
  unification) as columns maintained by another surviving cascade path into the same table, or
  the referenced identity is immutable (`allowIdentityUpdates = false`, non-abstract). Pruning
  such an edge to `NO ACTION` is safe: the surviving cascade keeps the shared column consistent,
  so the pruned FK never observes a mismatch (probe 6). These become `NoPropagation` and keep the
  **full composite** FK — RI is preserved without a second cascade path.

- **Live / independent** — the edge propagates an identity that can change independently and is
  *not* covered by any surviving cascade path. Pruning it to a `NO ACTION` composite FK would
  block real identity updates (probe 3), and neither a trigger (probe 3) nor an `INSTEAD OF`
  reorder (probe 5) can rescue it while identity columns remain in the FK.

### 4. Choose survivors and outcomes

For each candidate vertex, keep `ON UPDATE CASCADE` on exactly one live path (the deterministic
winner) and prune the rest:

| Final per-edge outcome | FK shape | `ON UPDATE` | Propagation mechanism |
|------------------------|----------|-------------|-----------------------|
| `NativeCascade` (surviving live edge) | full composite (identity parts + DocumentId) | `CASCADE` | engine cascade (probe 4) |
| `NoPropagation` (pruned, covered) | full composite | `NO ACTION` | none needed — covered by the surviving cascade (probe 6) |
| `TriggerFallback` (pruned, live, but cannot cascade) | `DocumentId`-only | `NO ACTION` | `MssqlIdentityPropagationTrigger` (today's mechanism, retained only here) |
| **derivation fails** (≥2 uncovered live paths into one vertex) | — | — | **fail fast** with a diagnostic |

The `TriggerFallback` shape is the *current* behavior; under this design it is used only for the
narrow set of edges that must be pruned yet remain live and coverable by direct trigger writes —
not as the blanket default. Where a surviving cascade can carry the value, the pruned edge keeps
its identity columns (`NoPropagation + FullComposite`) and RI is restored.

### 5. Fail fast when no safe pruning exists

If a candidate vertex has **two or more uncovered live incoming paths**, there is no legal SQL
Server DDL that preserves identity RI: SQL Server allows at most one cascade path (probe 1), a
`NO ACTION` composite FK cannot be trigger-rescued (probe 3), and `INSTEAD OF` is unavailable
(probe 5). DMS must **fail derivation with a diagnostic** that names the target table and the
conflicting live edges. This is precisely the case ODS prunes silently and incorrectly. Failing
here is safe because the schema that produces it should have been rejected at authoring time (see
the MetaEd follow-up), so the DMS check is a defense-in-depth backstop.

This maps onto the vocabulary already in the DMS-1128 placeholder: `MssqlPropagationMode`
(`NativeCascade` / `NoPropagation` / `TriggerFallback`), `MssqlFkShape` (`FullComposite` /
`DocumentIdOnly`), coverage/carrier reconciliation, and deterministic fail-fast on uncovered
cycles.

## Dialect scope decision (AC: "MSSQL only, or both?")

**Recommendation: physical FK pruning is emitted for SQL Server only; PostgreSQL keeps full
composite `ON UPDATE CASCADE` on every eligible edge.** ODS prunes for both engines, but ODS's
motivation is uniform DDL generation, not a PostgreSQL correctness need. PostgreSQL has no
multiple-cascade-paths restriction and no `NO ACTION`-before-trigger problem, so pruning on
PostgreSQL would only *remove* native RI enforcement for no benefit.

The **safety classification itself is dialect-agnostic**: the "≥2 uncovered live paths into one
vertex" condition describes a genuinely unsatisfiable identity-propagation requirement and should
be surfaced regardless of engine. The split is therefore:

- **Detect** the unsafe condition during model derivation for both dialects (schema soundness).
- **Emit** cascade pruning (`CASCADE` → `NO ACTION` rewrites) only for SQL Server DDL.
- **Prevent** the condition at authoring time in MetaEd (below), so neither engine encounters it
  in practice.

## Comparison to the ODS implementation

ODS (`UpdateCascadeTopLevelEntityEnhancer.ts`) builds a graph from entities with
`allowPrimaryKeyUpdates`, follows identity / identity-rename references, finds vertices whose
`inEdges(...).length > 1`, sorts incoming edges by `odsTableId`, keeps the first, and marks the
rest `odsCausesCyclicUpdateCascade = true` (rendered as `NO ACTION`). It has **no handling** for
the case where more than one incoming edge is a live, uncovered identity source — it prunes by
sort order regardless, which can silently drop a cascade that was actually required (the failure
mode called out in DMS-1129).

DMS keeps the deterministic graph/sort skeleton but adds the **coverage classification** (step 3)
and the **fail-fast** (step 5): a covered edge is pruned safely; an uncovered live conflict is a
hard error, never a silent prune.

## Migration from the current code

- `ReferenceConstraintPass.ResolveOnUpdate` — currently returns `NoAction` for **all** SQL Server
  reference FK updates. Under this design it returns `Cascade` for `NativeCascade` survivors and
  `NoAction` for pruned edges, driven by the new classification.
- `ReferenceConstraintPass` `mssqlTriggerHandlesPropagation` branch — currently drops identity
  columns from the FK for every abstract / `allowIdentityUpdates` target. Under this design the
  `DocumentId`-only shape is emitted **only** for `TriggerFallback` edges; `NativeCascade` and
  covered `NoPropagation` edges keep the full composite FK.
- `DeriveTriggerInventoryPass.EmitMssqlIdentityPropagationTriggers` — currently emits a
  propagation trigger for every eligible target. Under this design triggers are emitted only for
  `TriggerFallback` edges.
- New derivation output: per-edge `MssqlPropagationMode` / `MssqlFkShape` plus pruning/coverage
  diagnostics, and a hard derivation error for uncovered conflicts.

PostgreSQL emission is unchanged.

## Follow-up work

- **MetaEd** (authoring guard): disallow `allow primary key updates` configurations that yield a
  vertex with ≥2 uncovered live cascade paths — i.e. where no FK can be safely pruned — so unsafe
  schemas are rejected before they reach DMS.
- **DMS** (implementation): implement the classification, deterministic pruning, full-composite
  restoration on kept/covered edges, `TriggerFallback` narrowing, and fail-fast derivation.
- **DMS-1128**: reconcile with this design (it already anticipates being superseded by DMS-1129
  and references this file).

## Non-goals

- No production implementation is part of the DMS-1129 spike; only this design, the empirical
  confirmation, and the follow-up tickets.
- No change to PostgreSQL cascade behavior.
- No change to the UUIDv5 referential-identity or abstract-identity maintenance triggers; only the
  identity-*value* propagation mechanism for SQL Server is in scope.
