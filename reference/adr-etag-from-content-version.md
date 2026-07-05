# ADR: Derive `_etag` from `ContentVersion` instead of a content hash

**Status:** Accepted — implemented in the relational backend; the three design docs
(`update-tracking.md`, `transactions-and-concurrency.md`, `flattening-reconstitution.md`) have been
updated to match. \
**Amended 2026-07-04:** `profileCode` removed from the `If-Match` comparison to restore legacy
compatibility — see [Amendment (2026-07-04)](#amendment-2026-07-04-profilecode-removed-from-if-match-comparison). \
**Amended 2026-07-05:** unquoted `If-Match` values accepted as equivalent to quoted ones for legacy
compatibility — see [Amendment (2026-07-05)](#amendment-2026-07-05-unquoted-if-match-values-accepted-as-equivalent-to-quoted). \
**Amended 2026-07-05:** bare `If-Match: *` honored as an existence precondition per RFC 7232 §3.1 —
see [Amendment (2026-07-05, wildcard)](#amendment-2026-07-05-if-match-wildcard-matching). \
**Date:** 2026-06-30 (accepted 2026-07-03) \
**Deciders:** Development team (signed off 2026-07-03). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This ADR and the supporting code analysis were drafted with substantial AI assistance. The findings reflect the source code as it existed on 2026-06-30 and must be re-verified before implementation. Accountability for the decision rests with the development team.

## Executive Summary

A deep code analysis reveals that the `_etag` calculation requires an extra database read, which may have a noticeable impact across high volume operations. At the same time, the application is calculating a `ContentVersion` (numeric) for every update. This number could be used as the `_etag` instead.

The only additional benefit provided with the more complex current path is that it would guarantee etag equality for the same payload when pushed to multiple servers, or in the case of a `DELETE` followed by `POST` of the same body. However, this is not a requirement of an Ed-Fi API. Indeed, the legacy Ed-Fi ODS/API likewise does not satisfy that requirement. Consequently, this ADR pushes the code base to use the `ContentVersion` as the basis for the `_etag`.

Additionally, this ADR brings the `_etag` formatting in line with (HTTP) RFC 7232's requirements by serving different `_etag` values for each representation.

## Context

In the redesigned relational backend, `_etag` is computed as a SHA-256 hash of the canonical resource-state JSON (object properties ordinally ordered, arrays preserved, minified UTF-8, base64; server fields `id`, `link`, `_etag`, `_lastModifiedDate` excluded). The same algorithm is used by the current JSON-document backend.

A throughput review of the high-concurrency POST path (a client full-sync issuing tens of thousands of POSTs to `/students` and `/studentSchoolAssociations`) identified the per-write computation of this hash as the single most impactful bottleneck. In the relational backend the hash cannot be computed from the request body, because the persisted canonical document can differ from the request (collection merges, identity cascades, normalization). To obtain it, the write path performs a full hydrate-materialize-hash **readback** of the just-written document — a multi-statement query plus JSON reconstruction — **inside the write transaction, before `COMMIT`**, solely to populate a response header. The POST/PUT response body is `null`, so none of the materialized document is otherwise used.

Two facts make a change to the `_etag` contract feasible and attractive now:

1. **The content-hash `_etag` has not yet shipped** in the redesigned backend. Changing the client-visible `If-Match` value is therefore not a breaking change against deployed clients — it is a design choice still available at no compatibility cost.
2. **Content-addressability is not a firm requirement.** The team confirmed the deployment model does not require `_etag` to be identical for identical content across full rebuilds/migrations, or across heterogeneous instances/engines (e.g. blue-green with separate stores, or PostgreSQL and SQL Server replicas serving byte-identical etags).

> [!NOTE]
> Stephen has confirmed that this is _not_ a requirement by inspecting actual legacy ODS/API behavior. Thus while the idea has merit, it is unnecessary.

## Decision drivers

- Reduce or eliminate the per-write readback cost on the high-concurrency write path, and the row-lock window it extends.
- Preserve correct optimistic-concurrency (`If-Match`) semantics.
- Adhere strictly to RFC 7232 strong-validator semantics for `ETag` / `If-Match`.
- Maintain cross-engine (PostgreSQL / SQL Server) determinism.
- Avoid high-risk refactors and contracts that are hard to maintain.
- Respect the backend redesign's stated priority of correctness over speed; do not trade away a required property for throughput.

## Considered options

### Option 1 — Move the readback after `COMMIT`

Keep the hash; run the hydrate-materialize-hash readback after the transaction commits rather than before.

- **Pros:** Lowest risk; preserves the hash byte-for-byte; shrinks the row-lock window (concurrent re-POSTs to the same key stop serializing on hydration time).
- **Cons:** Does not reduce total work per write — the readback still runs, only outside the lock; throughput-per-core is unchanged. A post-commit read failure needs a defined fallback (the write already succeeded).Additionally, introduces a potential race condition:

  1. Client A: `BEGIN … writes … COMMIT`. At `COMMIT`, A's row lock on the `dms.Document` row (the `FOR UPDATE` Bottleneck) is released.
  2. Client B: acquires the row, updates the same resource, `COMMIT`s.
  3. Client A: post-commit readback runs under ReadCommitted → reads the latest committed state, which is now B's.

     So A's 201/200 response carries B's `_etag` and `_lastModifiedDate` (and, on a GET-style materialization, B's body), not the state A actually wrote.

### Option 2 — Compute the hash in-memory from merged write state

Build the canonical JSON from the merged write state at persist time and hash it in-process, eliminating the readback.

- **Pros:** Removes the readback entirely while preserving the hash contract; aligns the relational backend with how the JSON backend already works.
- **Cons:** Substantial, high-risk refactor. At persist time, memory holds a _flattened relational view_ (`RelationalWriteMergeResult` / per-table `FlattenedWriteValue[]`), the prior current state, and the request body — but **no canonical JSON document**. JSON reconstitution (`DocumentReconstituter` + `RelationalReadMaterializer`) runs only off hydrated _read_ rows. Implementing this requires a new serialization layer over flattened rows and a proof that its output is byte-for-byte identical to the read-path output on both engines; the lossy flatten/merge step (null-vs-omitted, value normalization) is exactly where divergence would hide. (Note: DB-stamped `ContentLastModifiedAt` is _not_ a blocker, because `_etag` excludes `_lastModifiedDate`; the blocker is the absence of an in-memory canonical JSON form.)

### Option 3 — Persist a `ContentEtag` column

Compute the hash once at write, store it on `dms.Document`, and serve it on reads from the column.

- **Pros:** Retires the per-_read_ rehash (helps GET-by-id and GET-collection).
- **Cons:** Does not help the POST write hot path on its own — the hash must still be computed at write time (via Option 1 or 2). Cannot be maintained by a DB trigger, because canonical-ordered SHA-256 is not reproducible identically across PostgreSQL and SQL Server, so it must be computed app-side. Useful only as a read-path complement to Option 1 or 2.

### Option 4 — Derive `_etag` from `ContentVersion` (CHOSEN)

Serve `_etag` from `dms.Document.ContentVersion`, the monotonic per-document change counter, abandoning the content hash.

- **Pros:** Cheapest possible — no readback, no hash; the value is already returned by `INSERT … RETURNING` and present on the row at zero marginal cost. Eliminates the bottleneck for both reads and writes. Satisfies every _written_ requirement for `_etag`. Retires the per-_read_ rehash (helps GET-by-id and GET-collection).
- **Cons:** Loses content-addressability and cross-instance content identity (both confirmed out of scope). Couples `If-Match` to the change-version counter — but this is moot, since `ChangeVersion` (equal to `ContentVersion`) is already a client-visible field, so the etag exposes nothing new.
- **Variant 4b:** Serve an opaque `hash(documentUuid + ContentVersion)` instead of the raw integer, to keep the etag opaque and able to diverge from `ChangeVersion` later. Not adopted now (marginal benefit, since the counter is already public), but recorded as a low-cost future option.

### Rejected alternative — Use `_lastModifiedDate`

Derive `_etag` from a the stored `ContentLastModifiedAt` timestamp.

- Viable on performance (a stored stamp, no readback — same win as Option 4), and clears the cheap / profile-stable / `If-Match`-capable bars.
- **Rejected because `ContentVersion` strictly dominates it:**
  - **Uniqueness:** a monotonic counter never collides; two writes to the same document within one clock tick produce identical timestamps and thus an identical etag across a real content change, risking an `If-Match` false-match and a lost update.
  - **Cross-engine determinism:** `now()` / `getutcdate()` are clock-based (skew, NTP, differing resolution), while a `bigint` counter is deterministic and strictly increasing.
  - Cascade-sensitivity is a _tie_ (the descriptor stamping trigger updates `ContentVersion` and `ContentLastModifiedAt` in the same statement), so the timestamp offers no advantage there.

## Decision

Adopt **Option 4**: derive `_etag` from `ContentVersion`.

`ContentVersion` satisfies every requirement the redesign documents actually state for `_etag`:

- **Resource-state-sensitive** — bumps on representation change, including identity cascades (subject to the follow-up below).
- **No-op-suppressed** — left unbumped when an inbound write changes nothing.
- **Stored, not response-derived** — read directly from the row.
- **Profile- and `link`-insensitive at the stamp level** — `ContentVersion` is a stored stamp taken before projection and response decoration. (The _served_ etag deliberately re-introduces profile/format/link sensitivity via `variantKey` for RFC 7232 compliance — see "ETag format and HTTP validator semantics (RFC 7232)".)
- **Cross-engine** — a deterministic `bigint`.

The only requirements it does not meet are content-addressability and cross-instance content identity, both confirmed out of scope. The change removes the bottleneck at its root rather than relocating it (Option 1) or accepting a high-risk refactor (Option 2).

One redesign requirement is **deliberately reversed**: the redesign specifies a profile/link-insensitive `_etag`, whereas this ADR makes the _served_ etag representation-sensitive for strict RFC 7232 compliance (see the next section). The underlying stamp (`ContentVersion`) remains profile/link-insensitive; only the served etag gains the `variantKey`.

## ETag format and HTTP validator semantics (RFC 7232)

RFC 7232 §2.1 distinguishes strong and weak validators. A strong validator must change whenever the representation changes in any way — any two responses sharing the etag are byte-for-byte identical for that representation — while a weak validator promises only semantic equivalence. This design requires **strong** validators: RFC 7232 §3.1 mandates the _strong_ comparison function for `If-Match`, and weak (`W/`-prefixed) etags never satisfy strong comparison, so a weak etag would make every `If-Match` precondition fail and break the optimistic-concurrency protection this design depends on. Etags are therefore served as strong entity-tags — quoted, with no `W/` prefix. Weakening is **not** an available fallback.

A bare `ContentVersion` is a strong validator only when each resource state maps to a single served byte-representation. That condition does not hold: the served bytes already vary by **profile** (readable-profile projection removes fields; selected per request) and by **link mode** (`ResourceLinksOptions.Enabled`; server configuration), and may vary by **format / media type** in the future (e.g. XML). A bare counter would assign the same etag to these byte-different representations, violating strong-validator semantics and conditional-GET cache correctness.

**Prescription — the etag MUST be `"{ContentVersion}-{variantKey}"`.** `variantKey` is a short, deterministic, stable token encoding every byte-affecting representation selector in scope — at minimum the response **format / media type**, the active **profile** (or its absence), and the **link mode**. This keeps each representation's etag distinct (strict RFC 7232 §2.1 adherence) while staying cheap: it composes the counter with a small key and performs no hashing of the document body. The `variantKey` is adopted **now**, not deferred, so cache and conditional-request behavior is correct across profiles and link modes from the start and no later contract change is required.

**`ContentVersion` MUST be treated as a string.** HTTP entity-tags are opaque, quoted strings (e.g. `ETag: "5-json"`). Neither the server nor clients may interpret the `ContentVersion` portion as a number: serialize it as a string in the `_etag` body field and as a quoted value in the `ETag` header, compare it as an opaque string (RFC strong comparison is character-by-character), and document it to clients as opaque so they never parse it or compare it numerically.

**`If-Match` comparison (decided).** The _served_ etag carries the full `variantKey`, but `If-Match` evaluation compares only the **state-significant projection** of the tag. The origin server mints these tags and may therefore compare them with knowledge of their structure — etag opacity binds clients, not the server. Two `variantKey` components, **`format`** and **`linkFlag`**, encode only how the representation is rendered: they never reflect resource state and never change as the result of a `PUT`/`DELETE` (a `DELETE` has no representation at all). Comparing them would produce spurious `412`s across representation variants that denote the _same_ state, so they are **excluded** from the `If-Match` match. The retained, compared components are `ContentVersion`, `schemaEpoch`, and **`profileCode`**.

> [!WARNING]
> **Superseded 2026-07-04.** The `profileCode`-significant decision described in this paragraph and the next was reversed for legacy compatibility. `profileCode` is **no longer** compared during `If-Match`; a cross-profile or profiled-vs-unprofiled `If-Match` now matches whenever `ContentVersion` and `schemaEpoch` agree. See [Amendment (2026-07-04)](#amendment-2026-07-04-profilecode-removed-from-if-match-comparison).

**`profileCode` is deliberately significant for `If-Match` (decision made 2026-07-01).** A readable profile changes which fields the client actually saw, so an etag obtained under profile A is treated as distinct from one obtained under profile B: a cross-profile `If-Match` returns `412` even when `ContentVersion` is unchanged. This is a chosen scoping guarantee, not an accident of encoding, and it is the point on which this design departs from the "compare only `ContentVersion`" lenient option.

Concretely, the precondition passes **if and only if** the client's `If-Match` tag and the server-composed expected tag agree on `ContentVersion`, `schemaEpoch`, and `profileCode`; differences in `format` or `linkFlag` are ignored. This preserves optimistic-concurrency safety — the ignored components cannot mask a state change — while avoiding false `412`s from link/format variance (e.g. an etag read from a links-on instance used to guard a write on a links-off instance). It is a deliberate, documented refinement of RFC 7232 §3.1: the _served_ etags remain full strong validators for conditional **GET** / `If-None-Match` cache correctness, and only the _write-time_ `If-Match` comparison is projected to the state-significant subset.

### `variantKey` encoding (specification)

`variantKey` is a dot-delimited, fixed-order, lowercase ASCII token of four components. All characters are drawn from `[a-z0-9_]` plus the `.` separator — all valid `etagc` characters (RFC 7232 §2.3), containing no `"` or `\`.

```
variantKey = schemaEpoch "." format "." profileCode "." linkFlag
etag-value = ContentVersion "-" variantKey          ; opaque; never parsed numerically
ETag       = DQUOTE etag-value DQUOTE               ; quotes are HTTP framing only
```

Components, in fixed order:

1. **`schemaEpoch`** — the first 8 lowercase hex characters of the in-force `EffectiveSchemaHash`. Captures every rendering input that is _not_ the document state itself: the resource's field set / ordering and all profile _definitions_. A schema or profile-definition change rotates this segment, correctly invalidating prior etags whose bytes are no longer reproducible. (Team option: substitute a coarser API-standard-version token if per-schema-hash invalidation is judged too aggressive; the hash is the strict-correct choice, because a profile redefinition genuinely changes the served bytes for an unchanged `ContentVersion`.)
2. **`format`** — a stable one-or-two-char code for the response media type, from a fixed server-side registry. Defined today: `j` = `application/json`. Reserve further codes (e.g. `x` = XML) as formats are added. Never derive this from the raw media-type string at runtime; map through the registry so codes stay stable.
3. **`profileCode`** — `_` when no profile is applied; otherwise the readable profile's stable compile-time index within the current `MappingSet` (a non-negative integer, e.g. `3`). Indices need only be stable within a `schemaEpoch`; because any profile redefinition rotates `schemaEpoch`, the index unambiguously identifies the profile for that epoch. No hashing required.
4. **`linkFlag`** — `l` when `ResourceLinksOptions.Enabled` is true (links emitted), `n` when false.

Examples (`ContentVersion` = 5, schema epoch `a1b2c3d4`):

| Representation | `_etag` body value | `ETag` header |
|---|---|---|
| JSON, no profile, links on | `5-a1b2c3d4.j._.l` | `"5-a1b2c3d4.j._.l"` |
| JSON, profile #3, links off | `5-a1b2c3d4.j.3.n` | `"5-a1b2c3d4.j.3.n"` |
| (future) XML, no profile, links on | `5-a1b2c3d4.x._.l` | `"5-a1b2c3d4.x._.l"` |

Rules:

- The opaque-tag value is everything between the quotes (`5-a1b2c3d4.j._.l`); the double-quotes are HTTP framing only. The `_etag` JSON body field carries the **unquoted** value; the `ETag` / `If-Match` headers the server **emits** carry it **quoted**. On **input**, however, the server accepts an `If-Match` value whether or not it is quoted (see [Amendment (2026-07-05)](#amendment-2026-07-05-unquoted-if-match-values-accepted-as-equivalent-to-quoted)); this asymmetry is deliberate, for legacy compatibility. A bare `If-Match: *` is handled separately as an RFC 7232 §3.1 wildcard, not as an opaque tag (see [Amendment (2026-07-05, wildcard)](#amendment-2026-07-05-if-match-wildcard-matching)).
- `ContentVersion` and every `variantKey` component are treated as **opaque strings** — no component is parsed or compared numerically.
- All components are always present (use `_` / `n` for "none" / "off") so the token has a fixed shape and is never ambiguous.
- The server recomputes the full tag deterministically from request context (negotiated format, profile in effect, link mode) plus the loaded schema at three points — read response, write response, and `If-Match` precondition — with **no database dependency** and **no document hashing**. At the `If-Match` precondition the comparison is against the **state-significant projection** (`ContentVersion`, `schemaEpoch`, `profileCode`; `format` and `linkFlag` excluded) — see "`If-Match` comparison (decided)".

Cost: each etag is a string concatenation of the counter with four small, precomputable tokens. `schemaEpoch`, the `format` registry, and per-profile indices are computed once at schema load and cached; `linkFlag` is a config read. No per-document hashing occurs.

**Alternative (fixed-length opaque token).** If a uniform, fully-opaque etag is preferred over the debuggable structured form, set `variantKey` to the first 12 hex characters of `SHA-256("{schemaEpoch}|{format}|{profileCode}|{linkFlag}")`. This hashes only a tiny descriptor string (cacheable per variant), not the document, so it preserves the performance goal; it trades operator readability for fixed width. The structured form is recommended unless fixed-length tags are specifically required.

## Supporting findings (code review, 2026-06-30)

- **No-op detection does not depend on the etag hash.** The guarded no-op path (`DefaultRelationalWriteExecutor` ~lines 310-347; `RelationalWriteGuardedNoOp`) detects an unchanged body by flattened row-by-row value comparison (`ComparableValues.SequenceEqual`), not by hashing. The subsequent `SELECT "ContentVersion" … FOR UPDATE` (`RelationalWriteFreshnessChecker`) compares the live `ContentVersion` to the value observed at lookup — a concurrency guard, not the content-change test. This means a content hash is not needed for no-op detection. It also means the no-op success path currently **still pays the full readback** to build the response etag, so even unchanged re-POSTs (the common case in a re-sync) incur the bottleneck today; Option 4 eliminates that.

- **`ContentVersion` is always on and not configurable.** It is emitted as `bigint NOT NULL` defaulting to `NEXT VALUE FOR dms.ChangeVersionSequence` (`CoreDdlEmitter`), assigned by the database on every INSERT. No `appsettings` flag gates it; the `AppSettings` classes expose no change-tracking toggle. The etag therefore rests on a value guaranteed present on every write.

- **`_etag` is produced in two places today**, both of which this decision changes (see Consequences).

## Consequences

### Cross-backend implementation scope

- **JSON-document backend:** `InjectVersionMetadataToEdFiDocumentMiddleware` (Core) currently computes `_etag` by hashing the request body **before persistence** (`ResourceEtagFormatter.FormatEtag`). Because `ContentVersion` does not exist until the write, etag production must move to **after** the write (or the backend must return the counter for Core to format). Confirm this does not regress the JSON backend's current zero-readback property.
- **Relational backend:** `_etag` is computed from the materialized current/committed representation; it will instead come directly from the persisted/returned `ContentVersion`.
- **`If-Match` precondition check:** `RelationalCurrentEtagPreconditionChecker` currently re-hashes the materialized current state. Under Option 4 it becomes a `ContentVersion` comparison — and since `RelationalWriteFreshnessChecker` already fetches `ContentVersion … FOR UPDATE`, the two could converge into a single read.

### Design documentation

The redesign docs that currently _specify_ the content hash must be updated: `update-tracking.md`, `transactions-and-concurrency.md` (§"Serving API metadata" / §"Concurrency"), and `flattening-reconstitution.md` §6.4. The update must also (a) **reverse** the requirement that `_etag` be insensitive to readable-profile filtering and link decorations, and (b) specify the `"{ContentVersion}-{variantKey}"` format and the opaque-string requirement.

### Client-visible behavior

`_etag` values become compact, opaque, quoted strong entity-tags of the form `"{ContentVersion}-{variantKey}"` rather than hashes; the `ContentVersion` portion is treated as an opaque string, never a number. Acceptable because the contract has not shipped. The _served_ etag varies by profile, format, and link mode — a deliberate change from the redesign's original profile/link-insensitive etag, for RFC 7232 conditional-GET correctness. `If-Match` matching, however, compares only the **state-significant projection** (`ContentVersion`, `schemaEpoch`, `profileCode`): it is sensitive to **profile** (a cross-profile `If-Match` returns `412`) but **not** to `format` or `link` mode (see "`If-Match` comparison (decided)"). `If-None-Match` (weak comparison, conditional GET) continues to use the full served etag.

> [!WARNING]
> **Superseded 2026-07-04.** `profileCode` is no longer part of the `If-Match` projection; the compared components are now `ContentVersion` and `schemaEpoch` only. The _served_ etag is unchanged (it still carries the full `variantKey`, so conditional-GET / `If-None-Match` caching is unaffected). See [Amendment (2026-07-04)](#amendment-2026-07-04-profilecode-removed-from-if-match-comparison).

### What is given up

Content-addressability across rebuilds/migrations and identical etags across instances/engines. If either becomes a requirement later, the fallback is to keep the hash and pursue Option 1 (post-COMMIT) for writes plus Option 3 for reads, or invest in Option 2.

## Open question to resolve before implementation

Confirm that an **identity-update cascade into referrers** (e.g. a `StudentUniqueId` change rippling into every resource embedding that reference) bumps the referrers' `ContentVersion`. The cascade-stamping trigger observed in the DDL is **descriptor-specific**; the mechanism for general identity cascades (application DML or otherwise) must be verified. This requirement applies equally to the content-hash approach; if any cascade path fails to bump `ContentVersion`, that is a defect to fix regardless of the etag decision, and it is a correctness precondition for this ADR.

## Next steps

1. Team review of this ADR.
2. Confirm the identity-cascade stamping behavior above.
3. On acceptance, update the design docs listed under Consequences.
4. Implement the cross-backend etag-production change, converging the relational `If-Match` check onto the `ContentVersion` comparison.
5. Human review of design-doc and code changes before merge.

## Amendment (2026-07-04): `profileCode` removed from `If-Match` comparison

**Status:** Accepted — amends the 2026-07-01 "`profileCode` is deliberately significant" decision. \
**Date:** 2026-07-04 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code and the confirmed legacy ODS/API behavior as understood on 2026-07-04 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

The 2026-07-01 decision made `profileCode` a **state-significant** component of the `If-Match` comparison, so an etag obtained under one representation (profiled or unprofiled) would fail an `If-Match` against a write under a different representation, returning `412` even when the resource state was unchanged. This amendment **removes `profileCode` from the comparison**. The retained, compared components are now **`ContentVersion` and `schemaEpoch`** only; `format`, `linkFlag`, and now `profileCode` are all projected out.

The **served** etag is unchanged: reads and writes still emit the full `"{ContentVersion}-{variantKey}"` tag including `profileCode`, so conditional-**GET** / `If-None-Match` cache correctness (the actual RFC 7232 driver for representation-sensitive etags) is fully preserved. Only the write-time `If-Match` comparison is relaxed.

### Why

1. **Legacy compatibility.** The legacy Ed-Fi ODS/API serves a single etag per resource state and accepts it for `If-Match` regardless of the profile lens used on the read. Confirmed by inspection of the legacy code base: a client may take an etag from a profile-filtered (partial) read and successfully use it as the `If-Match` for a full/unprofiled write. The 2026-07-01 decision broke that contract.

2. **The break could hit a compliant client.** The most acute case is an **asymmetric (read-only) profile** — a profile that defines a readable content type but no writable one for a resource. Such a client is *forced* to write unprofiled (there is no writable content type to send), yet the only etag it can obtain comes from the profiled read. Under the 2026-07-01 rule this yields a spurious `412` that the client cannot avoid.

3. **`profileCode` significance added no lost-update protection.** `If-Match`'s purpose is to detect concurrent modification, which `ContentVersion` answers completely — it bumps on *any* state change, including profile-hidden persisted fields (verified by the "stale hidden-field" tests, which still `412` on the `ContentVersion` change). `profileCode` only ever flips a *no-conflict* case (identical `ContentVersion`) into a `412`, enforcing "write through the same lens you read through" — a representation-consistency constraint that `If-Match` is not designed to provide and that legacy never imposed. The field-clobbering concern the original decision cited is a property of PUT-with-profile semantics, not something `If-Match` can or should police.

`schemaEpoch` **remains** significant: a schema or profile-*definition* change genuinely changes the reproducible bytes for an unchanged `ContentVersion`, so invalidating prior etags across such a change is still correct. This does not affect the legacy scenario, which is same-schema.

4. **Realignment with the DMS-1005 story.** The update-tracking story (`epics/10-update-tracking-change-queries/03-if-match.md`, Answers 1.1 and 3.2) already resolved that `If-Match` must "compare against the same full-resource `_etag` used by unprofiled requests" and "ignore readable profile projection." The 2026-07-01 decision diverged from that resolution; this amendment restores it. The ADR's *separate* decision to serve **profile-variant** etags for conditional-GET / `If-None-Match` cache correctness still stands — that supersedes the story's acceptance criterion "profiled GET preserves the same `_etag` as unprofiled" and is unaffected by this amendment, because served etags and the `If-Match` comparison are deliberately decoupled.

### Scope of the code change

- The only production change is `EtagMatchProjection.Of`, which stops appending `profileCode` to the projection. Both write precondition sites (`RelationalCurrentEtagPreconditionChecker` and `RelationalWriteExecutionStateResolver`) and the shared DELETE precondition path route through this method and inherit the new behavior automatically. Descriptors are unaffected (their `profileCode` is always the no-profile code).
- Etag **composition** (`EtagComposer` / `VariantKeyFactory` / `ProfileVariantCode`) is untouched, preserving the profile-sensitive served etag.

### Consequence

Cross-profile and profiled-vs-unprofiled `If-Match` (and DELETE `If-Match`) now succeed whenever `ContentVersion` and `schemaEpoch` agree, matching legacy ODS/API behavior. Optimistic-concurrency safety is retained in full, because the removed component never reflected resource state.

## Amendment (2026-07-05): unquoted `If-Match` values accepted as equivalent to quoted

**Status:** Accepted — clarifies the RFC 7232 posture stated in "ETag format and HTTP validator semantics". \
**Date:** 2026-07-05 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code and the confirmed legacy ODS/API behavior as understood on 2026-07-05 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

RFC 7232 §2.3 defines an entity-tag as an `opaque-tag` that **must** be double-quoted (`opaque-tag = DQUOTE *etagc DQUOTE`), so a strict reading — the posture this ADR otherwise adopts — treats `If-Match: "5-a1b2c3d4.j._.l"` as valid and a bare `If-Match: 5-a1b2c3d4.j._.l` as malformed. The legacy Ed-Fi ODS/API, by contrast, does **not** enforce the quoting requirement on input: it accepts the quoted and unquoted forms interchangeably for `If-Match`.

This amendment adopts the legacy behavior as a **requirement**: on **input**, the DMS treats an unquoted `If-Match` value as equivalent to the same value quoted. Both forms resolve to the same opaque tag and are compared identically against the server-composed expected tag.

The server's **output** contract is unchanged: the DMS continues to **emit** `ETag` response headers as fully quoted strong entity-tags (no `W/` prefix), per RFC 7232. The relaxation applies only to what the server is willing to **parse** from a client, not to what it produces. Weak (`W/`-prefixed) validators remain rejected for `If-Match` regardless of quoting.

### Why

1. **Legacy compatibility, no downside.** Clients written against the legacy ODS/API may send `If-Match` without quotes. Rejecting those requests (or silently failing the precondition) would be a breaking behavior change for a population of already-conforming-enough clients, with no offsetting benefit — the unquoted value is unambiguous here because the opaque tag contains no whitespace, comma, or quote characters (`variantKey` draws only from `[a-z0-9_.]` and `ContentVersion` is digits), so there is no parsing hazard in accepting it.

2. **Postel's-law robustness.** Being liberal in what the server accepts while strict in what it emits is the standard, safe accommodation for a validator whose grammar the server fully controls. Accepting the unquoted form cannot weaken optimistic-concurrency safety: the value still has to match the state-significant projection of the current tag, so a wrong or stale tag still yields `412`.

### Scope of the code change

This decision is **already satisfied** by the current implementation; the amendment documents the intent rather than requiring new work:

- `EtagValue.TryParseHeaderValue` (`Core/Utilities/EtagValue.cs`) strips surrounding quotes when present and otherwise returns the bare value unchanged, while still rejecting `W/` weak tags.
- `WritePreconditionFactory.Create` (`Core/Backend/WritePreconditionFactory.cs`) routes every `If-Match` header — for PUT, POST-as-update, and DELETE — through that helper, so all write preconditions inherit the quoted/unquoted equivalence from a single parse site.
- Server emission (`EtagValue.ToHeaderValue`, `EtagComposer`) is untouched and continues to quote.

If future hardening ever tightens the parser, the quoted/unquoted equivalence for `If-Match` input must be preserved (or explicitly re-decided by the team) to avoid regressing legacy clients.

### Consequence

A client may send `If-Match` with or without surrounding quotes and receive identical precondition behavior, matching legacy ODS/API. The change is confined to input tolerance; the emitted `ETag` contract and RFC 7232 strong-comparison semantics are otherwise unchanged.

## Amendment (2026-07-05): `If-Match` wildcard matching

**Status:** Proposed — **requires a code change** (not yet implemented; contrast the two amendments above, which the code already satisfied). \
**Date:** 2026-07-05 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code as understood on 2026-07-05 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

RFC 7232 §3.1 defines a wildcard for `If-Match`: with the field-value `*`, the precondition is true if — and only if — the origin server currently has a representation for the target resource, regardless of its entity-tag. The grammar is `If-Match = "*" / 1#entity-tag`, so the wildcard is the **bare, unquoted** `*`, a production distinct from a (quoted) entity-tag.

The DMS-1005 story specified an exact opaque-string comparison that "must not normalize quotes, parse entity-tag lists, or otherwise reinterpret the value." As a result, a `*` is today compared literally against the composed tag `"{ContentVersion}-{variantKey}"`, never equals it, and **always produces `412` — even when the resource exists.** This amendment adopts wildcard handling as a requirement, superseding the story's blanket "no reinterpretation" stance for the `*` case only (as the earlier quote/unquote amendments already did for quoting).

**Requirement.** The DMS must treat a bare `If-Match: *` as a wildcard: the precondition succeeds when a current representation of the target exists and fails with `412` when it does not.

Per operation:

- **PUT** (update-by-id): resource exists → `*` satisfied, update proceeds. Missing target → **`412`** (the wildcard is an existence precondition and it fails). This is the one case where a missing target does **not** return `404`.
- **DELETE**: resource exists → `*` satisfied, delete proceeds. Missing target → **`412`**.
- **POST** upsert resolving to **update** (existing document): `*` satisfied, proceeds.
- **POST** upsert resolving to **insert** (new document): no current representation → **`412`**, consistent with the existing "POST + `If-Match` on a new document → 412" rule.

Outside the wildcard, status-code behavior is unchanged: a missing PUT/DELETE target without `If-Match`, or with a specific-tag `If-Match`, still returns **`404`** (per DMS-1005 Answer 1.5). Only a bare `*` converts a missing target into a `412`.

A wildcard never guards against concurrent modification — it matches any version and asserts only existence. That is exactly the RFC-defined meaning and a deliberately weaker guarantee than a specific-tag `If-Match`, selected by the client.

### Decisions

Three points were resolved when adopting this amendment:

1. **Missing target + `If-Match: *` returns `412`, not `404`.** The wildcard is an existence precondition; when the target does not exist the precondition genuinely fails, so `412` (not `404`) is the RFC-conformant and chosen response. Non-wildcard requests keep the existing `404` for missing targets.
2. **Only the bare, unquoted `*` is the wildcard.** A quoted `"*"` is treated as an ordinary opaque tag (which will simply mismatch), matching the RFC grammar `If-Match = "*" / 1#entity-tag`.
3. **This is a non-breaking enhancement.** Legacy Ed-Fi ODS/API has no wildcard `If-Match` concept, so no existing client could have relied on wildcard behavior; today's unconditional `412` is simply a defect for any RFC-conformant client that sends `*`.

### Why

1. **RFC 7232 §3.1 conformance.** The current unconditional `412` for `*` is a defect against the standard this ADR otherwise commits to.
2. **Client ergonomics.** `*` is the standard way to express "modify/delete only if it exists." Honoring it lets clients and tooling use the wildcard as intended instead of receiving a spurious `412`.
3. **No concurrency-safety loss.** `*` is explicitly an existence check, not a change detector, so honoring it removes no protection that a specific-tag `If-Match` provides.

### Scope of the code change

Unlike the 2026-07-04 (`profileCode`) and earlier 2026-07-05 (unquoted) amendments, this is **new work**:

- **Detect the wildcard before quote-stripping.** `WritePreconditionFactory.Create` (`Core/Backend/WritePreconditionFactory.cs`) should recognize a raw `If-Match` value of exactly `*` and produce a new typed arm — e.g. `WritePrecondition.IfMatchAny` — rather than routing it through `EtagValue.TryParseHeaderValue`. Only the bare `*` qualifies; a quoted `"*"` continues through the normal opaque-tag path.
- **Honor the wildcard in the precondition checkers.** `RelationalCurrentEtagPreconditionChecker`, `RelationalWriteExecutionStateResolver`, the DELETE precondition path, and `DescriptorWriteHandler` should treat `IfMatchAny` as satisfied whenever the target row is present — i.e., pass when the existence/row-lock step that already runs succeeds — bypassing `EtagMatchProjection` entirely, and fail with `412` when the target is absent (including the PUT/DELETE missing-target case, which the wildcard converts from `404` to `412`).
- **Preserve non-wildcard precedence:** new-document POST-insert → `412`; missing PUT/DELETE target without a wildcard → `404`.

### Consequence

`If-Match: *` becomes a working existence precondition — succeeding on any current version of an existing resource and returning `412` when the resource does not exist — instead of an unconditional `412` in all cases. Optimistic-concurrency behavior for specific-tag `If-Match` is unchanged.
