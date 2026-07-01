# Companion: proposed design-doc edits for the `ContentVersion` `_etag` ADR

**Status:** DRAFT — pending acceptance of `adr-etag-from-content-version.md`. Do
not apply until the ADR is accepted. This file does **not** modify any design
doc; it stages paste-ready replacement wording. \
**Date:** 2026-07-01 \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 (Claude Code).

> **AI-use disclosure.** Drafted with substantial AI assistance. Line numbers
> and quoted "current" text reflect the design docs as of 2026-06-30 and must be
> re-checked before applying (the source may have moved). Human review required.

This companion preserves the existing ownership model: per
`transactions-and-concurrency.md` (§"Change Queries (read-only watermark)"),
**`update-tracking.md` owns the stamping contract and how `_etag` is derived.**
So the full new definition (including the `variantKey` encoding) goes into
`update-tracking.md`; `transactions-and-concurrency.md` and
`flattening-reconstitution.md` are reduced to references plus the strong-validator
/ `If-Match` rules they each already cover.

Each block below gives the **target location**, the **current** text, and the
**proposed** replacement.

## 1. `update-tracking.md` — the owning contract

### 1a. Overview (current lines 14–18)

**Current:**

> - `_etag` and `_lastModifiedDate` MUST change when the full resource-state representation changes,
>   including when referenced identity values change. Readable profile filtering can change the
>   response shape, but it does not change the `_etag` surface. Server-generated response decorations
>   such as reference `link` objects are not resource state and do not participate in `_etag`
>   derivation.

**Proposed:**

> - `_lastModifiedDate` and `ChangeVersion` MUST change when the full resource-state representation
>   changes, including when referenced identity values change.
> - `_etag` MUST change whenever the served byte-representation changes. It is derived from
>   `ContentVersion` (which tracks resource-state change) **and** a `variantKey` that distinguishes
>   the byte-affecting representation selectors (response format/media type, active readable profile,
>   and `link` mode). Unlike `_lastModifiedDate`/`ChangeVersion`, `_etag` is therefore
>   representation-sensitive: two representations of the same stored state that differ in served bytes
>   (e.g. different readable profiles, or links on vs. off) MUST carry different `_etag` values, as
>   required for RFC 7232 strong validators.

### 1b. Requirements (current lines 34–36)

**Current:**

> 1. **Correctness**: `_etag` and `_lastModifiedDate` MUST change when the full resource-state
>    representation changes. Readable profile filtering and response-only decorations such as
>    reference `link` objects MUST NOT change `_etag`.

**Proposed:**

> 1. **Correctness**: `_lastModifiedDate` and `ChangeVersion` MUST change when the full
>    resource-state representation changes. `_etag` MUST change whenever the served byte-representation
>    changes — i.e. on resource-state change **and** on any change to the representation selectors
>    (format, readable profile, `link` mode).
> 2. **RFC 7232 strong validator**: `_etag` is served as a strong validator (unquoted in the
>    `_etag` body field, quoted in the `ETag` header, never `W/`-prefixed). `If-Match` uses strong
>    comparison over the tag's **state-significant projection** — `ContentVersion`, `schemaEpoch`, and
>    `profileCode`; the `format` and `linkFlag` components are excluded (see "Optimistic concurrency").
>    Weak validators would fail every `If-Match` and are not permitted.

### 1c. API `_etag` derivation (current line 63)

**Current:**

> API `_etag` is derived from the canonical resource-state JSON representation described below.

**Proposed:**

> API `_etag` is derived from `dms.Document.ContentVersion` and the response `variantKey`, as
> specified in "Serving API metadata (normative)" below. It is **not** a hash of the resource-state
> JSON.

### 1d. Serving API metadata (normative) (current lines 146–168)

**Current** (the `_etag(P)` bullet and the two paragraphs that follow):

> - `_etag(P)` is a deterministic hash of the canonical JSON form of the full resource-state
>   document, before readable-profile projection. It is a resource-state validator, not a
>   response-shape validator:
>   - remove server-generated fields `id`, `link`, `_etag`, and `_lastModifiedDate`, recursively
>     canonicalize object properties using ordinal string ordering while preserving array order,
>     serialize the canonical form as minified UTF-8, compute `SHA-256` over those bytes, and encode
>     the hash as base64.
>   - readable-profile responses preserve the same full-resource `_etag`; profile filtering changes
>     which fields are returned, but not the concurrency validator.
>   - `DataManagement:ResourceLinks:Enabled` does not affect `_etag`; `link` subtrees are excluded
>     from hashing whether they are present in the response or stripped by the flag.
>
> This design does not compute metadata from dependency scans at read time. […] implementations reuse the same `_etag`.
>
> Interaction with `dms.DocumentCache` (when enabled): the cache stores the caller-agnostic pre-profile
> document and its full-resource `_etag`/`_lastModifiedDate`. […] `link` is excluded from canonicalization in both flag states.

**Proposed:**

> - `_etag(P)` is composed, not hashed:
>
>   ```
>   _etag-value = ContentVersion(P) "-" variantKey
>   ETag header = DQUOTE _etag-value DQUOTE      ; quotes are HTTP framing only
>   ```
>
>   - `ContentVersion(P) = dms.Document.ContentVersion`, serialized as an **opaque string** (never
>     interpreted numerically by server or client).
>   - `variantKey` is the deterministic representation token defined in "`variantKey` encoding"
>     below. It makes `_etag` distinct per served byte-representation, satisfying RFC 7232
>     strong-validator semantics.
>   - `_etag` MUST be computed with **no database readback and no document hashing**: it is a string
>     composition of a stored counter and precomputed representation tokens.
>
> This design does not compute `_etag` from the document body or from dependency scans at read time.
> Representation-state change is tracked by stored `ContentVersion`/`ContentLastModifiedAt`; `_etag`
> additionally reflects the representation selectors via `variantKey`.
>
> Interaction with `dms.DocumentCache` (when enabled): the cache stores the caller-agnostic pre-profile
> document keyed by `(DocumentId, ContentVersion)`. The cache does **not** store a single materialized
> `_etag`, because `_etag` is representation-specific; instead the server composes `_etag` per request
> from the cached `ContentVersion` and the request's `variantKey`. Freshness is judged on
> `ContentVersion` alone.

### 1e. NEW subsection to insert after 1d: `variantKey` encoding (normative)

> #### `variantKey` encoding (normative)
>
> `variantKey` is a dot-delimited, fixed-order, lowercase ASCII token of four always-present
> components. All characters are drawn from `[a-z0-9_]` plus the `.` separator — all valid `etagc`
> characters (RFC 7232 §2.3); it contains no `"` or `\`.
>
> ```
> variantKey = schemaEpoch "." format "." profileCode "." linkFlag
> ```
>
> 1. **`schemaEpoch`** — the first 8 lowercase hex characters of the in-force `EffectiveSchemaHash`.
>    Captures every rendering input that is not the document state itself (resource field set/ordering
>    and all profile *definitions*). A schema or profile-definition change rotates this segment,
>    correctly invalidating prior `_etag` values whose bytes are no longer reproducible.
> 2. **`format`** — a stable code for the response media type from a fixed server-side registry
>    (`j` = `application/json` today; reserve e.g. `x` for XML). MUST NOT be derived from the raw
>    media-type string at runtime.
> 3. **`profileCode`** — `_` when no readable profile applies; otherwise the readable profile's stable
>    compile-time index within the current `MappingSet`. Indices need be stable only within a
>    `schemaEpoch` (profile redefinition rotates `schemaEpoch`).
> 4. **`linkFlag`** — `l` when `DataManagement:ResourceLinks:Enabled` is true, `n` when false.
>
> Example: `_etag` body value `5-a1b2c3d4.j._.l`; `ETag` header `"5-a1b2c3d4.j._.l"`.
>
> The server MUST recompute the full `_etag` deterministically from request context (negotiated
> format, profile in effect, `link` mode) plus the loaded schema at read response, write response,
> and `If-Match` evaluation, with no database dependency. At `If-Match` evaluation the server compares
> only the **state-significant projection** of the tag (`ContentVersion`, `schemaEpoch`, `profileCode`;
> `format` and `linkFlag` excluded) — see "Optimistic concurrency".

### 1f. Optimistic concurrency (current lines 184–187)

**Current:**

> - GET returns `_etag` as the deterministic `SHA-256` hash of the current canonical full
>   resource-state JSON representation, with readable profile filtering and server-generated response
>   decorations such as `link` excluded from ETag-surface selection.
> - PUT/DELETE validates `If-Match` by comparing the client’s `_etag` to the current deterministic hash for that `DocumentId`.

**Proposed:**

> - GET returns `_etag` as `"{ContentVersion}-{variantKey}"` for the representation actually served
>   (see "Serving API metadata"). It is a strong validator.
> - PUT/DELETE validates `If-Match` using strong comparison over the tag's **state-significant
>   projection**. The backend reads the current `ContentVersion` and composes the expected tag from
>   the request's `variantKey`, then compares it to the client's `If-Match` while **ignoring the
>   `format` and `linkFlag` components** — these encode only how the representation is rendered, never
>   resource state, and never change on a write. The compared components are `ContentVersion`,
>   `schemaEpoch`, and `profileCode`. `profileCode` **is** significant: a readable profile changes
>   which fields the client saw, so a cross-profile `If-Match` returns `412 Precondition Failed` even
>   when `ContentVersion` is unchanged. A mismatch on any compared component returns `412`. (The served
>   `ETag` still carries the full `variantKey`; only the write-time comparison is projected, so
>   conditional-GET / `If-None-Match` caching stays byte-correct.) A client presents the `_etag` it
>   obtained for the representation it is acting on.

## 2. `transactions-and-concurrency.md` — reduce to references + rules

### 2a. Representation update tracking bullet (current lines 328–330)

**Current:**

> - **Representation update tracking (`_etag/_lastModifiedDate`, `ChangeVersion`)**
>   - `_lastModifiedDate` and `ChangeVersion` are served from stored stamps on `dms.Document`; `_etag` is computed from the deterministic canonical JSON form of the full resource-state document those stamps track, before readable profile projection and excluding response decorations such as `link`.

**Proposed:**

> - **Representation update tracking (`_etag/_lastModifiedDate`, `ChangeVersion`)**
>   - `_lastModifiedDate` and `ChangeVersion` are served from stored stamps on `dms.Document`. `_etag`
>     is composed as `"{ContentVersion}-{variantKey}"` (see `update-tracking.md`, "Serving API
>     metadata") — a strong validator that is representation-sensitive, computed with no readback or
>     hashing.

### 2b. Concurrency (optimistic `If-Match`) (current lines 334–338)

**Current:**

> With stored representation stamps:
>
> - GET returns `_etag` as the deterministic `SHA-256` hash of the current canonical full resource-state JSON representation, before readable profile projection and excluding response decorations such as `link`.
> - PUT/DELETE `If-Match` validation is row-local:
>   - compare the request `_etag` to the current deterministic hash for that `DocumentId`;
>   - if mismatched, return `412 Precondition Failed`.

**Proposed:**

> With stored representation stamps:
>
> - GET returns `_etag` as `"{ContentVersion}-{variantKey}"` for the served representation (see
>   `update-tracking.md`). It is an RFC 7232 strong validator: quoted in `ETag`, never `W/`.
> - PUT/DELETE `If-Match` validation is row-local and uses strong comparison over the tag's
>   **state-significant projection**:
>   - read the current `ContentVersion` for that `DocumentId` and compose the expected tag from the
>     inbound request's representation context;
>   - compare it to the request `_etag`, **excluding** the `format` and `linkFlag` components
>     (representation-encoding only) and **retaining** `ContentVersion`, `schemaEpoch`, and
>     `profileCode` (profile is significant — a cross-profile `If-Match` yields `412`);
>   - if mismatched on any retained component, return `412 Precondition Failed`.

### 2c. Profile ETag-surface statement (current line 357)

**Current:**

> - Correctness for accepted profile writes still relies on the same full-resource `If-Match` / `ContentVersion` guard described above; profile projection does not create a separate ETag surface, and no new API surface is required.

**Proposed:**

> - Correctness for accepted profile writes relies on the `If-Match` / `ContentVersion` guard
>   described above. Profile is a **significant** input to that guard: different readable profiles
>   yield different `_etag` values, and a cross-profile `If-Match` returns `412` even when
>   `ContentVersion` is unchanged. (`format` and `linkFlag`, by contrast, are excluded from the
>   `If-Match` comparison.) No new API surface is required.

### 2d. `dms.DocumentCache` freshness mentions (current lines ~490 and ~501)

**Current (line ~490):**

> - compare the cached representation stamp (for example, cached `ContentVersion` plus the cached materialized `_etag`) to the current `dms.Document` stamp,

**Current (line ~501):**

> 1. Keep `dms.DocumentCache` rows tagged with the applied representation stamp (for example, the applied `ContentVersion` plus the derived materialized `_etag`) to enforce the freshness contract above.

**Proposed (both):** drop the "materialized `_etag`" from the freshness stamp; `_etag` is
representation-specific and composed at serve time.

> - compare the cached `ContentVersion` to the current `dms.Document.ContentVersion`,
>
> 1. Keep `dms.DocumentCache` rows tagged with the applied `ContentVersion` to enforce the freshness
>    contract above; `_etag` is composed per request from `ContentVersion` + `variantKey` and is not
>    stored in the cache.

## 3. `flattening-reconstitution.md` §6.4 — stop hashing on the read path

### 3a. The `_etag` reconstitution bullet (current lines 917–920)

**Current:**

> - compute `_etag` as `SHA-256` over the canonical JSON form of the resource-state document
>   (excluding server-generated fields `id`, `link`, `_etag`, and `_lastModifiedDate`), where object
>   properties are recursively ordered canonically and arrays preserve element order; it must not be
>   generated as “now” or from ad hoc dependency scans at read/materialization time.

**Proposed:**

> - serve `_etag` as `"{ContentVersion}-{variantKey}"` (see `update-tracking.md`, "Serving API
>   metadata"). `_etag` MUST NOT be computed by hashing the reconstituted document: it is composed
>   from `dms.Document.ContentVersion` and the response `variantKey`, so reconstitution performs no
>   per-document hashing.

### 3b. Audit note

Search `flattening-reconstitution.md` for any remaining reference to "canonical JSON … for `_etag`"
purposes (e.g. the canonicalization list near current line 387–389 that mentions
`id`/`_etag`/`_lastModifiedDate`). Those canonical-JSON-for-etag rationales become obsolete; confirm
each remaining mention is for a still-valid purpose (e.g. no-op storage-space comparison) and not for
`_etag` derivation.

## Cross-cutting note for reviewers

These edits **reverse** the previously normative requirement that readable-profile filtering and
`link` mode do not change `_etag`. That reversal is the deliberate, RFC-driven choice recorded in the
ADR (strong-validator semantics require a distinct `_etag` per served byte-representation). Reviewers
who prefer the prior profile-insensitive behavior should resolve that at the ADR level, not here.
