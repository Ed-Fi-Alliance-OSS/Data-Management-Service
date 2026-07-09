# ADR: Derive `_etag` from `ContentVersion` instead of a content hash

**Status:** Accepted — implemented in the relational backend; the three design docs
(`update-tracking.md`, `transactions-and-concurrency.md`, `flattening-reconstitution.md`) have been
updated to match. \
**Amended 2026-07-04:** `profileCode` removed from the `If-Match` comparison to restore legacy
compatibility — see [Amendment (2026-07-04)](#amendment-2026-07-04-profilecode-removed-from-if-match-comparison). \
**Amended 2026-07-05:** unquoted `If-Match` values accepted as equivalent to quoted ones for legacy
compatibility — see [Amendment (2026-07-05)](#amendment-2026-07-05-unquoted-if-match-values-accepted-as-equivalent-to-quoted). \
**Amended 2026-07-05:** bare `If-Match: *` honored as an existence precondition per RFC 9110 §3.1 —
see [Amendment (2026-07-05, wildcard)](#amendment-2026-07-05-if-match-wildcard-matching). \
**Amended 2026-07-06:** `If-None-Match` support added — conditional-GET `304` plus a write create-guard —
see [Amendment (2026-07-06)](#amendment-2026-07-06-if-none-match-support). \
**Amended 2026-07-07:** the served descriptor `_etag` is now profile-sensitive for conditional-GET
correctness — see [Amendment (2026-07-07, descriptor profile etag)](#amendment-2026-07-07-descriptor-served-etag-varies-by-readable-profile). \
**Amended 2026-07-08:** a final `ContentVersion` read is restored — relocated into the persister,
after every table mutation — because the root-insert stamp is stale once child-table writes fire
stamp triggers; see [Amendment (2026-07-08, final ContentVersion read)](#amendment-2026-07-08-final-contentversion-read-relocated-into-the-persister). \
**Amended 2026-07-08:** the `variantKey` `profileCode` is a SHA-256 prefix of the profile *name*, not
a compile-time index — this hashes the profile descriptor, never the representation, so it upholds the
original no-representation-hash decision; see [Amendment (2026-07-08, profileCode hash)](#amendment-2026-07-08-profilecode-encodes-a-hash-of-the-profile-name). \
**Date:** 2026-06-30 (accepted 2026-07-03) \
**Deciders:** Development team (signed off 2026-07-03). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This ADR and the supporting code analysis were drafted with substantial AI assistance. The findings reflect the source code as it existed on 2026-06-30 and must be re-verified before implementation. Accountability for the decision rests with the development team.

## Executive Summary

A deep code analysis reveals that the `_etag` calculation requires an extra database read, which may have a noticeable impact across high volume operations. At the same time, the application is calculating a `ContentVersion` (numeric) for every update. This number could be used as the `_etag` instead.

The only additional benefit provided with the more complex current path is that it would guarantee etag equality for the same payload when pushed to multiple servers, or in the case of a `DELETE` followed by `POST` of the same body. However, this is not a requirement of an Ed-Fi API. Indeed, the legacy Ed-Fi ODS/API likewise does not satisfy that requirement. Consequently, this ADR pushes the code base to use the `ContentVersion` as the basis for the `_etag`.

Additionally, this ADR brings the `_etag` formatting in line with (HTTP) RFC 9110's requirements by serving different `_etag` values for each representation.

## Context

In the redesigned relational backend, `_etag` is computed as a SHA-256 hash of the canonical resource-state JSON (object properties ordinally ordered, arrays preserved, minified UTF-8, base64; server fields `id`, `link`, `_etag`, `_lastModifiedDate` excluded). The same algorithm is used by the current JSON-document backend.

A throughput review of the high-concurrency POST path (a client full-sync issuing tens of thousands of POSTs to `/students` and `/studentSchoolAssociations`) identified the per-write computation of this hash as the single most impactful bottleneck. In the relational backend the hash cannot be computed from the request body, because the persisted canonical document can differ from the request (collection merges, identity cascades, normalization). To obtain it, the write path performs a full hydrate-materialize-hash **readback** of the just-written document — a multi-statement query plus JSON reconstruction — **inside the write transaction, before `COMMIT`**, solely to populate a response header. The POST/PUT response body is `null`, so none of the materialized document is otherwise used.

Two facts make a change to the `_etag` contract feasible and attractive now:

1. **The content-hash `_etag` has not yet shipped** in the redesigned backend. Changing the client-visible `If-Match` value is therefore not a breaking change against deployed clients — it is a design choice still available at no compatibility cost.
2. **Content-addressability is not a firm requirement.** The team confirmed the deployment model does not require `_etag` to be identical for identical content across full rebuilds/migrations, or across heterogeneous instances/engines (e.g. blue-green with separate stores, or PostgreSQL and SQL Server replicas serving byte-identical etags).

> [!NOTE]
> Stephen has confirmed that this is *not* a requirement by inspecting actual legacy ODS/API behavior. Thus while the idea has merit, it is unnecessary.

## Decision drivers

- Reduce or eliminate the per-write readback cost on the high-concurrency write path, and the row-lock window it extends.
- Preserve correct optimistic-concurrency (`If-Match`) semantics.
- Adhere strictly to RFC 9110 strong-validator semantics for `ETag` / `If-Match`.
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
- **Cons:** Substantial, high-risk refactor. At persist time, memory holds a *flattened relational view* (`RelationalWriteMergeResult` / per-table `FlattenedWriteValue[]`), the prior current state, and the request body — but **no canonical JSON document**. JSON reconstitution (`DocumentReconstituter` + `RelationalReadMaterializer`) runs only off hydrated *read* rows. Implementing this requires a new serialization layer over flattened rows and a proof that its output is byte-for-byte identical to the read-path output on both engines; the lossy flatten/merge step (null-vs-omitted, value normalization) is exactly where divergence would hide. (Note: DB-stamped `ContentLastModifiedAt` is *not* a blocker, because `_etag` excludes `_lastModifiedDate`; the blocker is the absence of an in-memory canonical JSON form.)

### Option 3 — Persist a `ContentEtag` column

Compute the hash once at write, store it on `dms.Document`, and serve it on reads from the column.

- **Pros:** Retires the per-*read* rehash (helps GET-by-id and GET-collection).
- **Cons:** Does not help the POST write hot path on its own — the hash must still be computed at write time (via Option 1 or 2). Cannot be maintained by a DB trigger, because canonical-ordered SHA-256 is not reproducible identically across PostgreSQL and SQL Server, so it must be computed app-side. Useful only as a read-path complement to Option 1 or 2.

### Option 4 — Derive `_etag` from `ContentVersion` (CHOSEN)

Serve `_etag` from `dms.Document.ContentVersion`, the monotonic per-document change counter, abandoning the content hash.

- **Pros:** Cheapest possible — no readback, no hash; the value is already returned by `INSERT … RETURNING` and present on the row at zero marginal cost. Eliminates the bottleneck for both reads and writes. Satisfies every *written* requirement for `_etag`. Retires the per-*read* rehash (helps GET-by-id and GET-collection).
- **Cons:** Loses content-addressability and cross-instance content identity (both confirmed out of scope). Couples `If-Match` to the change-version counter — but this is moot, since `ChangeVersion` (equal to `ContentVersion`) is already a client-visible field, so the etag exposes nothing new.
- **Variant 4b:** Serve an opaque `hash(documentUuid + ContentVersion)` instead of the raw integer, to keep the etag opaque and able to diverge from `ChangeVersion` later. Not adopted now (marginal benefit, since the counter is already public), but recorded as a low-cost future option.

### Rejected alternative — Use `_lastModifiedDate`

Derive `_etag` from a the stored `ContentLastModifiedAt` timestamp.

- Viable on performance (a stored stamp, no readback — same win as Option 4), and clears the cheap / profile-stable / `If-Match`-capable bars.
- **Rejected because `ContentVersion` strictly dominates it:**
  - **Uniqueness:** a monotonic counter never collides; two writes to the same document within one clock tick produce identical timestamps and thus an identical etag across a real content change, risking an `If-Match` false-match and a lost update.
  - **Cross-engine determinism:** `now()` / `getutcdate()` are clock-based (skew, NTP, differing resolution), while a `bigint` counter is deterministic and strictly increasing.
  - Cascade-sensitivity is a *tie* (the descriptor stamping trigger updates `ContentVersion` and `ContentLastModifiedAt` in the same statement), so the timestamp offers no advantage there.

## Decision

Adopt **Option 4**: derive `_etag` from `ContentVersion`.

`ContentVersion` satisfies every requirement the redesign documents actually state for `_etag`:

- **Resource-state-sensitive** — bumps on representation change, including identity cascades (subject to the follow-up below).
- **No-op-suppressed** — left unbumped when an inbound write changes nothing.
- **Stored, not response-derived** — read directly from the row.
- **Profile- and `link`-insensitive at the stamp level** — `ContentVersion` is a stored stamp taken before projection and response decoration. (The *served* etag deliberately re-introduces profile/format/link sensitivity via `variantKey` for RFC 9110 compliance — see "ETag format and HTTP validator semantics (RFC 9110)".)
- **Cross-engine** — a deterministic `bigint`.

The only requirements it does not meet are content-addressability and cross-instance content identity, both confirmed out of scope. The change removes the bottleneck at its root rather than relocating it (Option 1) or accepting a high-risk refactor (Option 2).

One redesign requirement is **deliberately reversed**: the redesign specifies a profile/link-insensitive `_etag`, whereas this ADR makes the *served* etag representation-sensitive for strict RFC 9110 compliance (see the next section). The underlying stamp (`ContentVersion`) remains profile/link-insensitive; only the served etag gains the `variantKey`.

## ETag format and HTTP validator semantics (RFC 9110)

RFC 9110 §2.1 distinguishes strong and weak validators. A strong validator must change whenever the representation changes in any way — any two responses sharing the etag are byte-for-byte identical for that representation — while a weak validator promises only semantic equivalence. This design requires **strong** validators: RFC 9110 §3.1 mandates the *strong* comparison function for `If-Match`, and weak (`W/`-prefixed) etags never satisfy strong comparison, so a weak etag would make every `If-Match` precondition fail and break the optimistic-concurrency protection this design depends on. Etags are therefore served as strong entity-tags — quoted, with no `W/` prefix. Weakening is **not** an available fallback.

A bare `ContentVersion` is a strong validator only when each resource state maps to a single served byte-representation. That condition does not hold: the served bytes already vary by **profile** (readable-profile projection removes fields; selected per request) and by **link mode** (`ResourceLinksOptions.Enabled`; server configuration), and may vary by **format / media type** in the future (e.g. XML). A bare counter would assign the same etag to these byte-different representations, violating strong-validator semantics and conditional-GET cache correctness.

**Prescription — the etag MUST be `"{ContentVersion}-{variantKey}"`.** `variantKey` is a short, deterministic, stable token encoding every byte-affecting representation selector in scope — at minimum the response **format / media type**, the active **profile** (or its absence), and the **link mode**. This keeps each representation's etag distinct (strict RFC 9110 §2.1 adherence) while staying cheap: it composes the counter with a small key and performs no hashing of the document body. The `variantKey` is adopted **now**, not deferred, so cache and conditional-request behavior is correct across profiles and link modes from the start and no later contract change is required.

**`ContentVersion` MUST be treated as a string.** HTTP entity-tags are opaque, quoted strings (e.g. `ETag: "5-json"`). Neither the server nor clients may interpret the `ContentVersion` portion as a number: serialize it as a string in the `_etag` body field and as a quoted value in the `ETag` header, compare it as an opaque string (RFC strong comparison is character-by-character), and document it to clients as opaque so they never parse it or compare it numerically.

**`If-Match` comparison (decided).** The *served* etag carries the full `variantKey`, but `If-Match` evaluation compares only the **state-significant projection** of the tag. The origin server mints these tags and may therefore compare them with knowledge of their structure — etag opacity binds clients, not the server. The compared components are `ContentVersion` and `schemaEpoch`. The `variantKey` components **`format`**, **`profileCode`**, and **`linkFlag`** encode representation selectors rather than resource state, so they are excluded from the write-time `If-Match` match. This preserves optimistic-concurrency safety — ignored components cannot mask a state change because any persisted change advances `ContentVersion` — while avoiding false `412`s across representation variants that denote the same state. The *served* etags remain full strong validators for conditional **GET** / `If-None-Match` cache correctness, and only the write-time `If-Match` comparison is projected to the state-significant subset

### `variantKey` encoding (specification)

`variantKey` is a dot-delimited, fixed-order, lowercase ASCII token of four components. All characters are drawn from `[a-z0-9_]` plus the `.` separator — all valid `etagc` characters (RFC 9110 §2.3), containing no `"` or `\`.

```
variantKey = schemaEpoch "." format "." profileCode "." linkFlag
etag-value = ContentVersion "-" variantKey          ; opaque; never parsed numerically
ETag       = DQUOTE etag-value DQUOTE               ; quotes are HTTP framing only
```

Components, in fixed order:

1. **`schemaEpoch`** — the first 8 lowercase hex characters of the in-force `EffectiveSchemaHash`. Captures every rendering input that is *not* the document state itself: the resource's field set / ordering and all profile *definitions*. A schema or profile-definition change rotates this segment, correctly invalidating prior etags whose bytes are no longer reproducible. (Team option: substitute a coarser API-standard-version token if per-schema-hash invalidation is judged too aggressive; the hash is the strict-correct choice, because a profile redefinition genuinely changes the served bytes for an unchanged `ContentVersion`.)
2. **`format`** — a stable one-or-two-char code for the response media type, from a fixed server-side registry. Defined today: `j` = `application/json`. Reserve further codes (e.g. `x` = XML) as formats are added. Never derive this from the raw media-type string at runtime; map through the registry so codes stay stable.
3. **`profileCode`** — `_` when no profile is applied; otherwise the first 8 lowercase hex characters of `SHA-256(UTF-8(profileName))`, where `profileName` is the readable profile name. This hashes only the tiny, static profile-name descriptor — never the representation JSON — so it preserves the decision to stop hashing document bodies.
4. **`linkFlag`** — `l` when `ResourceLinksOptions.Enabled` is true (links emitted), `n` when false.

Examples (`ContentVersion` = 5, schema epoch `a1b2c3d4`):

| Representation | `_etag` body value | `ETag` header |
|---|---|---|
| JSON, no profile, links on | `5-a1b2c3d4.j._.l` | `"5-a1b2c3d4.j._.l"` |
| JSON, profiled, links off | `5-a1b2c3d4.j.9f1d2c3a.n` | `"5-a1b2c3d4.j.9f1d2c3a.n"` |
| (future) XML, no profile, links on | `5-a1b2c3d4.x._.l` | `"5-a1b2c3d4.x._.l"` |

Rules:

- The opaque-tag value is everything between the quotes (`5-a1b2c3d4.j._.l`); the double-quotes are HTTP framing only. The `_etag` JSON body field carries the **unquoted** value; the `ETag` / `If-Match` headers the server **emits** carry it **quoted**. On **input**, however, the server accepts an `If-Match` value whether or not it is quoted (see [Amendment (2026-07-05)](#amendment-2026-07-05-unquoted-if-match-values-accepted-as-equivalent-to-quoted)); this asymmetry is deliberate, for legacy compatibility. A bare `If-Match: *` is handled separately as an RFC 9110 §3.1 wildcard, not as an opaque tag (see [Amendment (2026-07-05, wildcard)](#amendment-2026-07-05-if-match-wildcard-matching)).
- `ContentVersion` and every `variantKey` component are treated as **opaque strings** — no component is parsed or compared numerically.
- All components are always present (use `_` / `n` for "none" / "off") so the token has a fixed shape and is never ambiguous.
- The server composes the full tag deterministically from `ContentVersion`, request context (negotiated format, profile in effect, link mode), and the loaded schema at three points: read response, write response, and `If-Match` precondition. Reads use the row's `ContentVersion`; write responses use the final `ContentVersion` returned by the persistence layer after all mutations, including the lightweight scalar read described in the 2026-07-08 amendment; `If-Match` preconditions compose the current served tag from the currently loaded row/version. No document hydration or document hashing is performed for etag construction. At the `If-Match` precondition the comparison is against the **state-significant projection** (`ContentVersion`, `schemaEpoch`; `format`, `profileCode`, and `linkFlag` excluded) — see "`If-Match` comparison (decided)" and the 2026-07-04 amendment.

Cost: each etag is a string concatenation of the counter with four small tokens. `schemaEpoch` and the `format` registry are derived from already-loaded schema metadata, `profileCode` is `_` or a short SHA-256 prefix of the readable profile name, and `linkFlag` is a config read. For write responses, the current implementation pays one scalar `ContentVersion` lookup after persistence-side mutations so trigger-stamped child changes are reflected in the returned etag. No per-document hashing occurs.

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

The redesign docs that currently *specify* the content hash must be updated: `update-tracking.md`, `transactions-and-concurrency.md` (§"Serving API metadata" / §"Concurrency"), and `flattening-reconstitution.md` §6.4. The update must also (a) **reverse** the requirement that `_etag` be insensitive to readable-profile filtering and link decorations, and (b) specify the `"{ContentVersion}-{variantKey}"` format and the opaque-string requirement.

### Client-visible behavior

`_etag` values become compact, opaque, quoted strong entity-tags of the form `"{ContentVersion}-{variantKey}"` rather than hashes; the `ContentVersion` portion is treated as an opaque string, never a number. Acceptable because the contract has not shipped. The *served* etag varies by profile, format, and link mode — a deliberate change from the redesign's original profile/link-insensitive etag, for RFC 7232 conditional-GET correctness. `If-Match` matching, however, compares only the **state-significant projection** (`ContentVersion`, `schemaEpoch`): it is insensitive to `profileCode`, `format`, and `linkFlag`, so representation-only differences do not cause a spurious `412` when the resource state is unchanged. `If-None-Match` (weak comparison, conditional GET) continues to use the full served etag

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

The **served** etag is unchanged: reads and writes still emit the full `"{ContentVersion}-{variantKey}"` tag including `profileCode`, so conditional-**GET** / `If-None-Match` cache correctness (the actual RFC 9110 driver for representation-sensitive etags) is fully preserved. Only the write-time `If-Match` comparison is relaxed.

### Why

1. **Legacy compatibility.** The legacy Ed-Fi ODS/API serves a single etag per resource state and accepts it for `If-Match` regardless of the profile lens used on the read. Confirmed by inspection of the legacy code base: a client may take an etag from a profile-filtered (partial) read and successfully use it as the `If-Match` for a full/unprofiled write. The 2026-07-01 decision broke that contract.

2. **The break could hit a compliant client.** The most acute case is an **asymmetric (read-only) profile** — a profile that defines a readable content type but no writable one for a resource. Such a client is *forced* to write unprofiled (there is no writable content type to send), yet the only etag it can obtain comes from the profiled read. Under the 2026-07-01 rule this yields a spurious `412` that the client cannot avoid.

3. **`profileCode` significance added no lost-update protection.** `If-Match`'s purpose is to detect concurrent modification, which `ContentVersion` answers completely — it bumps on *any* state change, including profile-hidden persisted fields (verified by the "stale hidden-field" tests, which still `412` on the `ContentVersion` change). `profileCode` only ever flips a *no-conflict* case (identical `ContentVersion`) into a `412`, enforcing "write through the same lens you read through" — a representation-consistency constraint that `If-Match` is not designed to provide and that legacy never imposed. The field-clobbering concern the original decision cited is a property of PUT-with-profile semantics, not something `If-Match` can or should police.

`schemaEpoch` **remains** significant: a schema or profile-*definition* change genuinely changes the reproducible bytes for an unchanged `ContentVersion`, so invalidating prior etags across such a change is still correct. This does not affect the legacy scenario, which is same-schema.

1. **Realignment with the DMS-1005 story.** The update-tracking story (`epics/10-update-tracking-change-queries/03-if-match.md`, Answers 1.1 and 3.2) already resolved that `If-Match` must "compare against the same full-resource `_etag` used by unprofiled requests" and "ignore readable profile projection." The 2026-07-01 decision diverged from that resolution; this amendment restores it. The ADR's *separate* decision to serve **profile-variant** etags for conditional-GET / `If-None-Match` cache correctness still stands — that supersedes the story's acceptance criterion "profiled GET preserves the same `_etag` as unprofiled" and is unaffected by this amendment, because served etags and the `If-Match` comparison are deliberately decoupled.

### Scope of the code change

- The only production change is `EtagMatchProjection.Of`, which stops appending `profileCode` to the projection. Both write precondition sites (`RelationalCurrentEtagPreconditionChecker` and `RelationalWriteExecutionStateResolver`) and the shared DELETE precondition path route through this method and inherit the new behavior automatically. Descriptors are unaffected (their `profileCode` is always the no-profile code).
- Etag **composition** (`EtagComposer` / `VariantKeyFactory` / `ProfileVariantCode`) is untouched, preserving the profile-sensitive served etag.

### Consequence

Cross-profile and profiled-vs-unprofiled `If-Match` (and DELETE `If-Match`) now succeed whenever `ContentVersion` and `schemaEpoch` agree, matching legacy ODS/API behavior. Optimistic-concurrency safety is retained in full, because the removed component never reflected resource state.

## Amendment (2026-07-05): unquoted `If-Match` values accepted as equivalent to quoted

**Status:** Accepted — clarifies the RFC 9110 posture stated in "ETag format and HTTP validator semantics". \
**Date:** 2026-07-05 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code and the confirmed legacy ODS/API behavior as understood on 2026-07-05 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

RFC 9110 §2.3 defines an entity-tag as an `opaque-tag` that **must** be double-quoted (`opaque-tag = DQUOTE *etagc DQUOTE`), so a strict reading — the posture this ADR otherwise adopts — treats `If-Match: "5-a1b2c3d4.j._.l"` as valid and a bare `If-Match: 5-a1b2c3d4.j._.l` as malformed. The legacy Ed-Fi ODS/API, by contrast, does **not** enforce the quoting requirement on input: it accepts the quoted and unquoted forms interchangeably for `If-Match`.

This amendment adopts the legacy behavior as a **requirement**: on **input**, the DMS treats an unquoted `If-Match` value as equivalent to the same value quoted. Both forms resolve to the same opaque tag and are compared identically against the server-composed expected tag.

The server's **output** contract is unchanged: the DMS continues to **emit** `ETag` response headers as fully quoted strong entity-tags (no `W/` prefix), per RFC 9110. The relaxation applies only to what the server is willing to **parse** from a client, not to what it produces. Weak (`W/`-prefixed) validators remain rejected for `If-Match` regardless of quoting.

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

A client may send `If-Match` with or without surrounding quotes and receive identical precondition behavior, matching legacy ODS/API. The change is confined to input tolerance; the emitted `ETag` contract and RFC 9110 strong-comparison semantics are otherwise unchanged.

## Amendment (2026-07-05): `If-Match` wildcard matching

**Status:** Proposed — **requires a code change** (not yet implemented; contrast the two amendments above, which the code already satisfied). \
**Date:** 2026-07-05 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code as understood on 2026-07-05 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

RFC 9110 §3.1 defines a wildcard for `If-Match`: with the field-value `*`, the precondition is true if — and only if — the origin server currently has a representation for the target resource, regardless of its entity-tag. The grammar is `If-Match = "*" / 1#entity-tag`, so the wildcard is the **bare, unquoted** `*`, a production distinct from a (quoted) entity-tag.

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

1. **RFC 9110 §3.1 conformance.** The current unconditional `412` for `*` is a defect against the standard this ADR otherwise commits to.
2. **Client ergonomics.** `*` is the standard way to express "modify/delete only if it exists." Honoring it lets clients and tooling use the wildcard as intended instead of receiving a spurious `412`.
3. **No concurrency-safety loss.** `*` is explicitly an existence check, not a change detector, so honoring it removes no protection that a specific-tag `If-Match` provides.

### Scope of the code change

Unlike the 2026-07-04 (`profileCode`) and earlier 2026-07-05 (unquoted) amendments, this is **new work**:

- **Detect the wildcard before quote-stripping.** `WritePreconditionFactory.Create` (`Core/Backend/WritePreconditionFactory.cs`) should recognize a raw `If-Match` value of exactly `*` and produce a new typed arm — e.g. `WritePrecondition.IfMatchAny` — rather than routing it through `EtagValue.TryParseHeaderValue`. Only the bare `*` qualifies; a quoted `"*"` continues through the normal opaque-tag path.
- **Honor the wildcard in the precondition checkers.** `RelationalCurrentEtagPreconditionChecker`, `RelationalWriteExecutionStateResolver`, the DELETE precondition path, and `DescriptorWriteHandler` should treat `IfMatchAny` as satisfied whenever the target row is present — i.e., pass when the existence/row-lock step that already runs succeeds — bypassing `EtagMatchProjection` entirely, and fail with `412` when the target is absent (including the PUT/DELETE missing-target case, which the wildcard converts from `404` to `412`).
- **Preserve non-wildcard precedence:** new-document POST-insert → `412`; missing PUT/DELETE target without a wildcard → `404`.

### Consequence

`If-Match: *` becomes a working existence precondition — succeeding on any current version of an existing resource and returning `412` when the resource does not exist — instead of an unconditional `412` in all cases. Optimistic-concurrency behavior for specific-tag `If-Match` is unchanged.

## Amendment (2026-07-06): `If-None-Match` support

**Status:** Proposed — implemented (pending sign-off). This is the first amendment to touch the read path. \
**Date:** 2026-07-06 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code as understood on 2026-07-06 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

The ADR's original analysis and every prior amendment addressed only `If-Match`. `If-None-Match` — advertised as an OpenAPI `header` parameter on GET-by-id in the API schema, but never read or enforced by the DMS — was left unimplemented. As a result a conditional GET silently returns `200` with a full body where a client would expect `304 Not Modified`, and there is no create-guard on writes. This amendment adopts **full `If-None-Match` support** per RFC 9110 §13.1.2 and §13.2.2, covering both of the header's roles.

`If-None-Match` is **not** a mirror of `If-Match`. RFC 9110 assigns it two distinct jobs, mandates the **weak** comparison function for it, and **inverts** the wildcard. The design below is deliberately consistent with the existing `If-Match` machinery where the semantics coincide (write-time state-significant projection, quoted/unquoted tolerance, bare-`*` detection) and deliberately diverges where RFC 9110 requires it (full-tag comparison on reads, weak comparison, inverted wildcard).

### Behavior

**Two behaviors.**

- **Conditional read** (GET-by-id; HEAD when/if supported). When the precondition is *false* — **any** of the client's tags matches the current representation — the server responds **`304 Not Modified`** with the current `ETag` header and no body. When *true* (none match), it responds `200` with the full representation as usual.
- **Write create-guard** (POST upsert, PUT update-by-id). When the precondition is *false*, the server responds **`412`**. The dominant, RFC-canonical form is `If-None-Match: *` = "proceed only if no current representation exists" (insert-only):
  - POST upsert resolving to an **existing** document → `412`; resolving to an **insert** → proceeds.
  - PUT (update-by-id) against an **existing** target → `412`; the PUT missing-target case is unchanged (`404`).

**Entity-tag list support (RFC 9110 §13.1.2).** `If-None-Match` accepts a **comma-separated list** of entity-tags (e.g., `If-None-Match: "tag1", "tag2", W/"tag3"`). The precondition is evaluated against each tag in the list: if **any** tag matches (using the weak comparison function — see below), the precondition is *false* (triggering `304` on reads or `412` on writes). A single tag is the degenerate case of a one-element list. The bare `*` wildcard cannot appear in a list — per RFC 9110 it is only valid as the sole value.

**Comparison basis — the read/write asymmetry (the crux of this amendment).**

- **Conditional reads compare the FULL served tag** — every `variantKey` component (`ContentVersion`, `schemaEpoch`, `format`, `profileCode`, `linkFlag`). This is precisely the cache-correctness guarantee the ADR already cites as the reason served etags are representation-complete: a client that cached a *JSON / profile-3 / links-off* body must **not** receive `304` when it re-requests the resource under a different profile, format, or link mode, because the served bytes genuinely differ.
- **Write-side `If-None-Match` compares the state-significant projection** (`ContentVersion`, `schemaEpoch`) through the existing `EtagMatchProjection.Of` — identical to `If-Match`, and for the same reason: representation variance (`format`, `profileCode`, `linkFlag`) must not spuriously flip a state precondition. A bare `*` compares nothing; it is a pure existence test.

Thus reads use the full tag and writes use the projection. This is intentional: it is the same deliberate decoupling of served-etag comparison from write-precondition comparison that the 2026-07-04 amendment introduced for `If-Match`, applied here in the opposite direction — for `If-None-Match`, reads are the *stricter* side.

**Comparison function — weak, per RFC 9110 §2.1.** `If-None-Match` uses the **weak** comparison function, in contrast to `If-Match`, which this ADR pins to strong comparison. Because the DMS only ever *emits* strong tags, the practical consequence is input tolerance: a `W/`-prefixed value on `If-None-Match` is accepted and its opaque-tag compared, whereas `If-Match` rejects weak tags (see "ETag format and HTTP validator semantics"). The quoted/unquoted equivalence ([Amendment 2026-07-05](#amendment-2026-07-05-unquoted-if-match-values-accepted-as-equivalent-to-quoted)) and the bare-`*` wildcard rule (below) carry over unchanged.

**Unquoted acceptance is required for legacy conditional-GET compatibility (ODS-6853).** Manual review of the legacy Ed-Fi ODS/API found a defect (tracked as **ODS-6853**): it returns `304` **only** when the client's `If-None-Match` value is **unquoted**, and fails to `304` when the value is quoted — even though it emits a (quoted) `ETag`. Clients that successfully use conditional GET against legacy therefore send the etag **unquoted**. For the DMS to be compatible with that installed client behavior, the DMS **must accept an unquoted `If-None-Match`** and treat it as equivalent to the quoted form. The DMS remains strictly RFC-correct on **output** (it emits quoted strong `ETag`s); the leniency is confined to **input**, exactly as for `If-Match` (2026-07-05). This makes the unquoted tolerance a **requirement**, not merely Postel's-law robustness: without it, a working legacy client would silently stop receiving `304` after migration.

**Wildcard `*` is inverted from `If-Match`.** `If-None-Match: *` means "the server has **no** current representation of the target":

- GET-by-id → `304` if the resource exists; normal processing (`200`, or `404` if absent) otherwise.
- Write → `412` if the resource exists; proceeds (create) if not.

This is the exact inverse of the `If-Match: *` wildcard ([Amendment 2026-07-05, wildcard](#amendment-2026-07-05-if-match-wildcard-matching)), where `*` asserts the resource **does** exist. Only the bare, unquoted `*` is the wildcard; a quoted `"*"` is an ordinary opaque tag that simply mismatches.

**Precedence when both headers are present (RFC 9110 §13.2.2).** `If-Match` is evaluated first; `If-None-Match` is evaluated only when `If-Match` is absent. A request carrying both (contradictory) preconditions therefore resolves in favor of `If-Match`.

### Per-operation summary

| Operation | `If-None-Match: *` | `If-None-Match: "<tag>"` or `"<tag1>", "<tag2>", ...` |
|---|---|---|
| GET-by-id, resource exists | `304` | `304` if **any** tag in the list matches the **full served tag**; else `200` |
| GET-by-id, resource absent | `404` (normal) | `404` (normal) |
| POST upsert → existing document | `412` | `412` if **any** tag's **projection** matches; else proceeds (update) |
| POST upsert → insert | proceeds (create) | proceeds (create) |
| PUT, target exists | `412` | `412` if **any** tag's **projection** matches; else proceeds (update) |
| PUT, target absent | `404` (unchanged) | `404` (unchanged) |

### Why

1. **Close the advertised-but-unimplemented gap.** The API schema already declares `If-None-Match` on GET-by-id, so a conforming client can reasonably send it expecting `304`. Today it is silently ignored — a latent defect, not a design choice.
2. **RFC 9110 conformance.** §13.1.2 defines both the conditional-read `304` and the write `412` behaviors; honoring them aligns the DMS with the standard this ADR otherwise commits to.
3. **Standard create-only idiom.** `If-None-Match: *` is the canonical way to express "create only if absent." Supporting it lets clients and tooling perform safe inserts without a separate existence check.
4. **No optimistic-concurrency change.** The write create-guard is an existence/inverse-match test, not a change detector; it removes no protection `If-Match` provides and does not alter `If-Match` behavior.
5. **Legacy conditional-GET compatibility (ODS-6853).** Legacy clients doing conditional GET send the etag unquoted (working around the ODS-6853 quoted-`If-None-Match` defect). Accepting an unquoted `If-None-Match` — which the DMS already does — preserves `304` behavior for those clients after migration; see the unquoted-acceptance note above.

### Decisions

1. **Reads compare the full served tag; writes compare the state-significant projection.** Reads are about representation/cache correctness (all `variantKey` components significant); writes are about resource state (`format`/`profileCode`/`linkFlag` projected out, exactly as for `If-Match`).
2. **`If-None-Match` uses weak comparison and accepts `W/` tags on input;** `If-Match` remains strong-only. Emission is unchanged — the server still emits only strong tags. In practice this means `W/"1-abc"` on `If-None-Match` is compared as if it were `"1-abc"` — only the opaque-tag portion matters.
3. **`If-None-Match` accepts a comma-separated list of entity-tags** per RFC 9110 §13.1.2. The precondition triggers (`304` or `412`) if **any** tag in the list matches. Each tag in the list is independently subject to weak comparison, `W/` stripping, and quoted/unquoted tolerance.
4. **Only the bare `*` is the wildcard, and it is inverted from `If-Match: *`** — it asserts *non-existence*. A quoted `"*"` is an ordinary (mismatching) opaque tag.
5. **`If-Match` takes precedence over `If-None-Match`** when both are present (RFC 9110 §13.2.2).
6. **Additive, non-breaking enhancement.** No existing DMS client can depend on the current non-behavior (the header is ignored today). Legacy conditional-GET behavior has now been reviewed: legacy `304`s only on an **unquoted** `If-None-Match` (ODS-6853). The DMS's design — accept quoted *and* unquoted input, emit quoted output — is a strict superset of the working legacy behavior, so a legacy client that receives `304` today continues to receive it against the DMS. This removes the last open uncertainty flagged when the amendment was first drafted.

### Scope of the code change

Like the 2026-07-05 wildcard amendment, this is **new work**, and it is the first amendment to add a **read-path** precondition.

- **Parse.** `WritePreconditionFactory.Create` (`Core/Backend/WritePreconditionFactory.cs`) recognizes `If-None-Match` (only when `If-Match` is absent — precedence) and produces a new `WritePrecondition.IfNoneMatch(Values, IsWildcard)` arm. It first detects the bare `*` wildcard; otherwise it splits the header value on commas (per RFC 9110 entity-tag list grammar) and parses each element through a **weak-tolerant parse** that strips `W/` prefixes, strips surrounding quotes, and tolerates an unquoted value (ODS-6853 compatibility) — distinct from `EtagValue.TryParseHeaderValue`, which rejects `W/` for `If-Match`. A single-tag header is the degenerate one-element case.
- **Write checkers.** `RelationalCurrentEtagPreconditionChecker`, `RelationalWriteExecutionStateResolver`, and `DescriptorWriteHandler` honor the new arm: a wildcard `IfNoneMatch` fails (`412`) when the target row is present and passes when absent; a specific-tag list `IfNoneMatch` fails when **any** tag's projection matches and passes otherwise — the logical inverse of the `If-Match` check, iterated over the list, reusing `EtagMatchProjection`. Because these are decided at several layers, all sites (including the deferred post-authorization path) must be covered and proven by PostgreSQL and SQL Server integration tests.
- **Read path (new).** A conditional-GET hook in the GET-by-id handler parses the client's `If-None-Match` list (weak comparison, `W/` stripping) and compares each tag against the full served tag; if **any** tag matches, it short-circuits to `304 Not Modified` — with the `ETag` header and no body. As implemented, the handler echoes the already-composed `_etag` from the materialized response body (which `RelationalReadMaterializer` composed via `EtagComposer` with all `variantKey` components) rather than recomposing it independently; this is functionally identical and cannot drift from read materialization. `If-Match` has no read-side equivalent today, so this is a genuinely new surface.
- **Emission unchanged.** `EtagValue.ToHeaderValue` / `EtagComposer` continue to emit fully quoted strong tags. The `304` response carries the current `ETag` and no `_etag` body field (there is no body).
- **DELETE is out of scope.** `If-None-Match` on DELETE is not a meaningful idiom (`*` would `412` whenever there is anything to delete); a DELETE carrying `If-None-Match` is ignored. It may be revisited if a concrete need arises.

### Consequence

`If-None-Match` becomes a working conditional-read validator (`304 Not Modified` on a cache hit) and a working write create-guard (`412` on `If-None-Match: *` against an existing resource), per RFC 9110. Optimistic-concurrency behavior for `If-Match` and the emitted `ETag` contract are both unchanged.

### Out of scope: `If-Modified-Since`, `If-Unmodified-Since`, `If-Range`

For the record, and to bound this amendment: the DMS still does **not** implement `If-Modified-Since`, `If-Unmodified-Since`, or `If-Range`, and this amendment does not add them.

- **`If-Modified-Since` / `If-Unmodified-Since`** are timestamp-based validators. The ADR's rejection of `_lastModifiedDate` as an etag basis — clock skew, sub-tick collisions, and cross-engine `now()` / `getutcdate()` nondeterminism (see "Rejected alternative — Use `_lastModifiedDate`") — applies equally to using `ContentLastModifiedAt` as a precondition validator. Date-based conditionals are therefore deliberately deferred in favor of the strong, `ContentVersion`-derived entity-tag; a client wanting a conditional read should use `If-None-Match`.
- **`If-Range`** is only meaningful alongside HTTP range requests, which the DMS does not support.

These may be revisited if a concrete client need arises; none is a conformance gap for the entity-tag-based conditional model this ADR establishes.

## Amendment (2026-07-07): descriptor served `_etag` varies by readable profile

**Status:** Accepted — closes a gap left open when the profile-sensitive served etag (see "ETag format and HTTP validator semantics (RFC 7232)") was implemented only for non-descriptor resources. \
**Date:** 2026-07-07 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code as understood on 2026-07-07 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

A feasibility spike confirmed that descriptors **are** subject to readable-profile projection on GET: `ProfileResolutionMiddleware` runs on the descriptor GET pipelines, `Handler/Utility.CreateReadableProfileProjectionContext` special-cases `IsDescriptor` to seed the identity-property allow-list, and `DescriptorReadHandler.MaterializeDescriptorDocument` already applied `IReadableProfileProjector.Project(...)` to the served descriptor body when a projection context was present. Despite that, the served descriptor `_etag` was composed via the fixed `DescriptorVariantKey.For(effectiveSchemaHash)`, whose `profileCode` is hardcoded to the no-profile sentinel `_` regardless of whether a profile actually projected the body. Two profile-different descriptor representations therefore shared one strong `ETag` — a violation of RFC 7232 §2.1 for the same reason the main ADR text gives for non-descriptor resources.

`DescriptorReadHandler` now composes the served descriptor etag through `IServedEtagComposer`, passing the active readable profile's name (when one actually projects the read) instead of the fixed descriptor variant key. `DescriptorDocumentMaterializer.Materialize` no longer composes the etag itself; it accepts the caller's precomposed string, so the handler alone decides profile-sensitivity — mirroring the pattern already used by the non-descriptor path (`RelationalReadMaterializer.ComposeEtag`). The condition that selects the profile name mirrors `RelationalDocumentStoreRepository.ShouldApplyReadableProfileProjection` exactly (`ExternalResponse` read mode **and** a non-null projection context), so the descriptor and non-descriptor read paths stay in lockstep.

### What did not change

- **Descriptor `If-Match` remains profile-insensitive.** `EtagMatchProjection.Of` (unaffected by this change) drops `profileCode` — along with `format` and `linkFlag` — from every precondition comparison, descriptor or not (see [Amendment (2026-07-04)](#amendment-2026-07-04-profilecode-removed-from-if-match-comparison)). A descriptor PUT/DELETE using an `If-Match` value obtained from a profiled GET still succeeds whenever `ContentVersion` and `schemaEpoch` agree, exactly as for non-descriptor resources.
- **Descriptor write-response etags remain unprofiled.** `DescriptorWriteHandler` already composed its response etag via `IServedEtagComposer` with `ProfileName: null` before this change and continues to do so; only the *read* path changes.

  > [!WARNING]
  > **Superseded 2026-07-08 — descriptor write-response etags are now profile-sensitive.** A follow-up
  > fix ("profile-code descriptor write response ETags") changed `DescriptorWriteHandler` to compose
  > every success-response etag via `IServedEtagComposer` with the request's `ProfileName` (not
  > `ProfileName: null`). A profiled descriptor POST/PUT therefore returns the same profile-variant
  > `_etag` a follow-up profiled GET serves — the write→read parity the descriptor integration tests
  > assert. This aligns the descriptor *write* path with the *read* path this amendment introduced.
  > Descriptor `If-Match` is unaffected and remains profile-insensitive (the bullet above): the served
  > etag gains `profileCode`, but `EtagMatchProjection.Of` still drops it from every precondition
  > comparison.
- `DescriptorVariantKey` is left in place: it remains the correct, still-used shorthand for the fixed "no profile, no links, JSON" variant in the descriptor *write*-side tests (`DescriptorWriteHandlerPreconditionTests`, `DescriptorWriteHandlerResponseEtagTests`), where the represented etag genuinely is always profile-insensitive. It is simply no longer invoked from descriptor *read* production code.

### Scope of the code change

- `DescriptorReadHandler` (`src/dms/backend/EdFi.DataManagementService.Backend/DescriptorReadHandler.cs`): constructor now takes `IServedEtagComposer` instead of `IEtagComposer`; the descriptor GET-by-id and query materialization paths compose the served etag from `ServedEtagContext` with the resolved profile name (or `null`) rather than the fixed `DescriptorVariantKey`.
- `DescriptorDocumentMaterializer` (`src/dms/backend/EdFi.DataManagementService.Backend/DescriptorDocumentMaterializer.cs`): `Materialize` now takes a precomposed `string? composedEtag` instead of `IEtagComposer` + `VariantKey`, and throws if an `ExternalResponse` read is materialized without one.
- Test updates: `DescriptorReadHandlerTests`, `DescriptorDocumentMaterializerTests`, and `DescriptorReadHandlerNamespaceAuthorizationTests` updated for the new signatures; the previous pinning test (which asserted a profiled and unprofiled descriptor read shared one `_etag`) was flipped to assert they now differ, and a case was added proving unprofiled descriptor reads still yield the `_` profile code. `DescriptorWriteHandlerPreconditionTests` gained PUT and DELETE cases proving a profile-obtained descriptor etag still satisfies `If-Match` against an unprofiled write.

### Consequence

Two profile-different descriptor GET representations now carry distinct strong `ETag` values, closing the RFC 7232 §2.1 gap for descriptors specifically (the main ADR text already closed it for non-descriptor resources). `If-Match` / `If-None-Match` semantics for descriptor writes are unaffected — a client may still read a descriptor under one profile and write it back unprofiled using the same etag.

## Amendment (2026-07-08): final `ContentVersion` read relocated into the persister

**Status:** Accepted — refines how Option 4's "serve `_etag` from the persisted `ContentVersion`"
is realized on the write path; the served-etag and `If-Match` contracts are unchanged. \
**Date:** 2026-07-08 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code as understood on 2026-07-08 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

Option 4 assumed the write-response etag could be composed from the `ContentVersion` "already
returned by `INSERT … RETURNING` … at zero marginal cost," and an intermediate refactor accordingly
**dropped the response reader's separate `SELECT ContentVersion`** and composed the etag from that
persist-time value. This amendment **restores a final `ContentVersion` read**, but relocates it out
of the response reader and into the persistence layer, where it runs **after every table mutation**.

Concretely:

- `RelationalWritePersistResult` carries the final `ContentVersion` (read after
  `ExecuteDeletesAsync` / `ExecuteUpsertsAsync` in `RelationalWriteNoProfilePersister.PersistAsync`
  via `ReadCommittedContentVersionAsync`).
- `DefaultRelationalWriteExecutor` composes the served write-response etag directly from
  `persistedTarget.ContentVersion` and issues **no** `dms.Document` query of its own. (Originally this
  ran through a `RelationalCommittedRepresentationReader` seam; once the readback was gone that type was
  an async interface wrapping only `IServedEtagComposer` + `ResourceLinksOptions`, so it was collapsed
  into the executor.)
- The guarded no-op path (no persister runs) composes from `guardedTarget.ObservedContentVersion`,
  which the freshness check already established.

### Why

1. **Correctness — the root-insert stamp is not the final `ContentVersion`.** Child-table
   deletes/upserts can fire stamp triggers that bump the owning root document's `ContentVersion`.
   The value returned by the root `INSERT` (the "zero-cost" value Option 4 anticipated) is captured
   before those child writes run, so composing the etag from it would emit a **stale** tag that does
   not match the `ContentVersion` a subsequent GET returns — breaking conditional-GET / `If-None-Match`
   cache correctness and the write→read etag parity the descriptor and non-descriptor tests assert.
   The final version is only known once every table operation has completed.

2. **Ownership / layering.** The persister owns the write boundary and is the only layer that knows
   when all persistence-side mutations are done. The final `ContentVersion` is **persistence
   metadata**, not response-materialization metadata, so it belongs on `RelationalWritePersistResult`.
   Moving it there makes the write contract explicit instead of hiding a database lookup inside a
   "read committed response" abstraction, and prevents future code from reintroducing representation
   hydration/hash work behind that abstraction.

3. **It does not give back the throughput goal, and preserves the path to fully reclaiming it.**
   The bottleneck this ADR removed was the hydrate-materialize-**hash** readback; the restored read
   is a single lightweight `ContentVersion` lookup, run after the mutations rather than inside a
   representation read. Placing it in the persister sets up the next optimization: the persister can
   later capture the final stamp directly from the DML/triggers and drop even this lookup, eliminating
   the round trip in the correct layer.

### What did not change

- The served-etag format (`"{ContentVersion}-{variantKey}"`) and the `If-Match` state-significant
  projection (`ContentVersion` + `schemaEpoch`) are untouched.
- No content hashing is reintroduced anywhere; the readback that Option 4 eliminated stays eliminated.
- `RelationalWritePersistResult.ContentVersion` defaults to `0` only for the incremental rollout;
  production persistence always sets a positive value, and `RelationalWritePersistedTargetValidator`
  rejects a non-positive committed `ContentVersion` on applied writes.

### Consequence

Write-response etags reflect the true post-commit `ContentVersion` — including bumps from child-table
stamp triggers — so a write's etag matches a follow-up GET's etag. The remaining per-write cost is a
single `ContentVersion` read located in the persistence layer, where it is visible and can be removed
later without touching the response contract.

## Amendment (2026-07-08): `profileCode` encodes a hash of the profile name

**Status:** Accepted — clarifies the `variantKey` encoding to match the implementation; the etag
contract (state derives from `ContentVersion`; representation selectors are appended) is unchanged. \
**Date:** 2026-07-08 \
**Deciders:** Development team (pending sign-off). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** This amendment and its supporting analysis were drafted with substantial AI assistance. Findings reflect the source code as understood on 2026-07-08 and must be human-reviewed before merge. Accountability for the decision rests with the development team.

### What changed

The [`variantKey` encoding](#variantkey-encoding-specification) specified `profileCode` as "the
readable profile's stable **compile-time index** within the current `MappingSet` (a non-negative
integer, e.g. `3`)." The implementation instead derives it as a short lowercase-hex **SHA-256 prefix
of the readable profile *name*** — `ProfileVariantCode.Of` returns `VariantKey.NoProfileCode` (`_`)
when no profile applies, otherwise the first 4 bytes (8 hex characters) of
`SHA-256(UTF-8(profileName))`. A served etag's `profileCode` is therefore `_` or an 8-hex token
(e.g. `5-a1b2c3d4.j.9f3a2b1c.n`), not a small integer.

The index form was not used because a `MappingSet` exposes no enumerable profile catalog from which to
assign stable ordinals. The name-hash prefix is deterministic and stable across processes and engines,
draws only from `etagc`-safe characters (`[0-9a-f]`), and needs no catalog — it identifies the profile
directly from the name already carried on the read/write request. This is the same hashing tradeoff the
ADR already sanctioned in its "Alternative (fixed-length opaque token)" note, applied to the
`profileCode` component only while `schemaEpoch`, `format`, and `linkFlag` remain structured.

### Why this does not violate the spirit of the original decision

This ADR's performance driver was eliminating the per-write hash of **the representation itself** — the
hydrate-materialize-hash readback that reconstructs the canonical resource-state JSON inside the write
transaction (see [Context](#context) and Option 2). Hashing the profile **name** is categorically
different:

- It hashes a **tiny, static descriptor string** (the profile's name), not the document body.
- It requires **no readback** and **no per-document work** — the name is known from request context,
  and the 8-hex code is a pure function of it, computable once and cacheable per
  (`schemaEpoch`, profile) variant.
- Its cost is constant and independent of resource size or child-collection count, so it cannot
  reintroduce the throughput bottleneck the ADR removed.

The etag's **state** signal still comes entirely from `ContentVersion`; the profile hash only
distinguishes **representations** so that two profile-different responses carry distinct strong
validators, exactly as RFC 7232 §2.1 requires (see
[ETag format and HTTP validator semantics](#etag-format-and-http-validator-semantics-rfc-7232)).

### What did not change

- The etag format `"{ContentVersion}-{variantKey}"` and the fixed four-component `variantKey` order.
- `If-Match` comparison, which projects `profileCode` out entirely (along with `format` and
  `linkFlag`) per [Amendment (2026-07-04)](#amendment-2026-07-04-profilecode-removed-from-if-match-comparison),
  so the profile-name hash never affects optimistic-concurrency behavior — it only affects served-etag
  distinctness for conditional **GET** / `If-None-Match`.
- No representation hashing is introduced anywhere; the readback the ADR eliminated stays eliminated.

### Consequence

The `variantKey` documentation now matches the shipped encoding: `profileCode` is `_` or an 8-hex
SHA-256 prefix of the profile name. The change is descriptive — it records how the etag is already
built and affirms that hashing the profile descriptor (not the representation) is consistent with the
ADR's core decision.

## References

- Design documents in this repository
- [RFC 9110: HTTP Semantics](https://www.rfc-editor.org/info/rfc9110/)
- [Ed-Fi-ODS Repository](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS)
- [Ed-Fi-ODS-Implementation Repository](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation)
- Manual testing of etag behavior in ODS/API 7.3
- ODS-6853 — legacy ODS/API returns `304` only for an unquoted `If-None-Match` (quoted values do not match); basis for the DMS unquoted-acceptance requirement
