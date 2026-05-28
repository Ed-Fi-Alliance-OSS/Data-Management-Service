---
jira: DMS-1165
jira_url: https://edfi.atlassian.net/browse/DMS-1165
---

# Slice 6: Relationship Auth ProblemDetails Hardening

## Purpose

Harden relationship authorization error handling so the EdOrg CRUD slices and People core metadata produce the exact RFC 9457 ProblemDetails shapes described in `auth.md`.

This slice should refine response mapping and tests. It should not introduce new authorization strategies or endpoint execution behavior.

## In Scope

- Consume the Slice 2 relationship authorization failure-set contract produced from `AUTH1` failures, including all failed OR-strategy/subject entries and their failure kinds.
- Do not assume one relationship `AUTH1` failure maps to one configured strategy index; relationship payloads may identify a failed OR group plus plan-relative strategy/subject ordinals.
- Format relationship authorization 403 responses per `auth.md` §"ProblemDetails".
- Translate securable element JSON paths to readable names, preferring MetaEd/readable names when available.
- Handle singular vs plural securable element error text.
- Format EdOrg claim values, including `none` and truncation after five claims followed by `...`.
- Aggregate distinct hints across multiple failing relationship OR strategies.
- Preserve strategy identity for invalid-data and element-required relationship failures.
- Unit, integration, and focused E2E tests for final response shape.

## Explicitly Out Of Scope

- New database authorization objects or DDL.
- New relationship strategy semantics.
- GET-many normal denial behavior; GET-many still filters rows rather than returning 403 for unauthorized rows.
- Implementing People CRUD endpoint execution.
- Implementing namespace, ownership, or view-based ProblemDetails beyond avoiding regressions in shared error infrastructure.

## ProblemDetails Cases

This slice owns final formatting for relationship authorization cases from `auth.md`:

- Relationship-based no relationships established with EdOrg claims.
- Relationship-based no relationships established without EdOrg claims where applicable to a relationship/custom-view-style check.
- Required relationship securable element uninitialized in existing data.
- Required relationship securable element missing from proposed data.
- Mixed failed relationship OR groups, using failure-kind precedence: existing stored-value invalid data, proposed-value element required, then no relationship established.
- Hint text appended to `detail`.
- Multiple distinct hints concatenated in deterministic configured strategy order.

## Acceptance Criteria

- A failed relationship auth check returns status 403 with:
  - `type`,
  - `title`,
  - `status`,
  - `detail`,
  - `errors`, and
  - `correlationId`
  matching the `auth.md` RFC 9457 contract.
- The versioned relationship `AUTH1` failure-set payload introduced by Slice 2 is handled reliably for PostgreSQL and SQL Server failure patterns.
- ProblemDetails formatting uses the full relationship failure DTO/failure set, including mixed no-relationship, stored-null, and proposed-value failure kinds, without re-querying authorization state.
- When a failed relationship OR group contains mixed failure kinds, the formatter selects the top-level ProblemDetails `type`, `detail`, and primary error text using this precedence: existing stored-value invalid data / element uninitialized; proposed-value element required; no relationship established / no matching authorization relationship.
- Lower-precedence failed entries do not hide or downgrade higher-precedence entries. If multiple entries share the selected precedence, their readable securable names, strategy identity, and hints are aggregated in deterministic configured order.
- If one relationship strategy fails, the mapper uses that strategy's readable securable element metadata and hints.
- If multiple OR relationship strategies fail, the mapper combines the relevant securable names and distinct hints without losing configured strategy order.
- A single readable securable element uses the singular error message form.
- Multiple readable securable elements use the plural error message form and deterministic name ordering.
- EdOrg claims render as `none` when the caller has no EdOrg claims.
- One EdOrg claim uses `claim`; multiple EdOrg claims use `claims`.
- More than five EdOrg claims render only the first five deterministic values followed by `...`.
- JSON paths such as `$.schoolReference.schoolId` render as `SchoolId` when no MetaEd/readable name is available.
- MetaEd/readable names carried by the auth core are preferred over fallback JSON path derivation.
- Stored-value failures use existing-data wording.
- Proposed-value failures use proposed-data wording.
- Relationship invalid-data and element-required cases use the specific `auth.md` relationship `type` values.
- Security-configuration failures remain 500 security configuration failures and are not converted into 403 authorization denials.

## Tests Required

### Unit tests

- PostgreSQL and SQL Server relationship `AUTH1` payload extraction regressions, using the shared Slice 2 parser/DTO contract.
- Formatting from full failed OR-strategy metadata, including multiple strategies and mixed failure kinds.
- Mixed failure-kind precedence chooses existing-data invalid-data over no-relationship failures, proposed-value element-required over no-relationship failures, and no-relationship only when no higher-precedence failure kind is present.
- Single securable element message.
- Multiple securable elements message.
- Readable name fallback from JSON path.
- MetaEd/readable name preference.
- No EdOrg claims formatting.
- One, five, and more-than-five EdOrg claim formatting.
- Multiple OR strategy hint aggregation with duplicate hints removed.
- Existing-data uninitialized and proposed-data missing relationship cases.

### Backend integration tests

- PostgreSQL and SQL Server unauthorized GET-by-id response body matches the final relationship ProblemDetails contract.
- PostgreSQL and SQL Server unauthorized POST-create response body matches the final relationship ProblemDetails contract.
- PostgreSQL and SQL Server unauthorized PUT response body distinguishes stored vs proposed value failures.
- PostgreSQL and SQL Server unauthorized DELETE response body matches the final relationship ProblemDetails contract.

### E2E tests

- Focused DMS E2E coverage validates the externally observable response shape for one stored-value operation and one proposed-value operation.

## Reviewer Focus

Reviewers should focus on wire compatibility with `auth.md`, not on authorization SQL semantics already owned by earlier slices.

## Clarifying Questions and Answers

### Questions 1

1. For relationship invalid-data and proposed element-required failures, `auth.md` gives singular examples only; what exact plural `detail` and `errors` wording should Slice 6 use when multiple same-precedence securable elements are selected?
2. For relationship proposed element-required failures, `auth.md` says `Error: (empty)`; should the final RFC 9457 body include `"errors": []`, omit the member, or emit strategy/securable diagnostic text?
3. When a relationship failure has no normalized EdOrg claims, should EdOrg-only and People relationship failures always use the EdOrg-claims wording with `(none)`, and reserve the "without EdOrg claims" existing/proposed wording for custom-view-style failures that truly do not use EdOrg claims?
4. Should authorization hints be appended only for no-relationship failures, or also for selected invalid-data and proposed element-required relationship failures when the failed entries carry auth-view hint metadata?
5. When multiple selected failures share the same displayed readable securable name across strategies or contributing paths, should the formatter de-duplicate by displayed name, preserve one entry per contributing path, or preserve one entry per failed strategy/subject?
6. For multiple selected invalid-data failures across different relationship strategies, should `errors` contain one message per strategy using each `{authorizationStrategyName}`, or one aggregated message that lists multiple strategy names?
7. Since People CRUD endpoint execution is out of scope but People core metadata is in scope, should Slice 6 add unit coverage for People-specific relationship failure DTOs, readable names, auth-view hints, and no-claims formatting even though integration/E2E coverage remains EdOrg-only?

### Answers 1

1. Use the existing `auth.md` singular wording for one selected readable securable name. For multiple existing-data invalid-data names, use `detail`: `Access to the requested data could not be authorized. The existing values of one or more of the following properties are required for authorization purposes: '{ReadableSecurableElement1}', '{ReadableSecurableElement2}'.` For multiple proposed element-required names, use `detail`: `Access to the requested data could not be authorized. The values of one or more of the following properties are required for authorization purposes: '{ReadableSecurableElement1}', '{ReadableSecurableElement2}'.`
2. Include the `errors` member as an empty array: `"errors": []`. Do not omit the member and do not add strategy/securable diagnostic text for proposed element-required failures; `auth.md` defines that case as having an empty error list.
3. Yes. EdOrg-only and People relationship failures should use the EdOrg-claims wording with claims rendered as `(none)` when the normalized EdOrg claim list is empty. Reserve the "without EdOrg claims" existing/proposed wording for custom-view-style failures that truly do not use EdOrg claims.
4. Append authorization hints for the selected top-level relationship failure kind whenever the selected failed entries carry hint metadata, including invalid-data and proposed element-required failures. Append distinct hints to `detail` after the selected base detail text, preserving configured strategy/subject order.
5. De-duplicate displayed readable securable names in the user-facing property list. Use first occurrence in deterministic configured strategy/subject/contributor order to establish ordering, and preserve the contributing path/strategy metadata internally for tests and strategy-specific error generation.
6. For selected invalid-data failures, emit one `errors` entry per selected configured relationship strategy entry in configured order, using that entry's `{authorizationStrategyName}`. If multiple selected subjects fail under the same configured strategy, aggregate their readable securable names in `detail` and keep a single strategy error for that strategy entry. Do not collapse multiple strategy names into one combined error sentence.
7. Yes. Add unit coverage for People relationship failure DTO mapping, readable-name selection, auth-view hint aggregation, and empty-claims formatting using the Slice 5 People metadata. Keep backend integration and E2E coverage focused on EdOrg CRUD response shapes until People CRUD endpoint execution is in scope.

### Questions 2

1. If the versioned compact `AUTH1` failure-set payload is missing, malformed, uses an unknown version, is truncated, or maps to plan-relative ordinals that do not exist in the relationship plan, what response should Slice 6 produce: canonical security-configuration 500, generic system 500, or a relationship 403 fallback?
2. For proposed-value relationship element-required failures, Answer 1.2 says the final body must include `"errors": []`, but the story also says to preserve strategy identity for element-required failures. Should that identity be preserved only in the internal DTO/unit assertions, with no strategy-specific text on the wire?
3. For final response-shape integration and E2E assertions, should `correlationId` be asserted only as a present/non-empty value matching the DMS correlation-id format, or should tests inject/fix the correlation id and compare the entire ProblemDetails body exactly?
4. Given Answer 1.3 reserves the "without EdOrg claims" wording for custom-view-style failures and custom view ProblemDetails are explicitly out of scope for Slice 6, should Slice 6 remove/avoid adding tests for `auth.md` §2.4 unless a shared formatter already covers it, leaving CRUD custom-view wire coverage to the view-based auth story?
5. When no-relationship failures are selected from multiple OR strategies, should the final `errors` array contain a single aggregated no-relationship message with de-duplicated readable names, or one message per selected failed strategy/subject?

### Answers 2

1. Produce the canonical security-configuration 500. A missing, malformed, unknown-version, truncated, or plan-ordinal-mismatched relationship `AUTH1` payload means the SQL-to-backend authorization contract cannot be trusted, so Slice 6 should fail closed with `urn:ed-fi:api:system:configuration:security` rather than inventing a relationship 403 fallback or returning a generic system error. Include a deterministic security-configuration error entry that identifies the invalid relationship authorization failure payload; keep raw provider text in logs only.
2. Yes. Preserve strategy identity for proposed element-required failures in the parsed failure set, external relationship failure DTO, and unit assertions, but do not emit strategy-specific `errors` text on the wire. The final ProblemDetails body still uses `"errors": []`; only allowed wire-visible additions are the selected `detail` text and any distinct authorization hints already allowed by Answer 1.4.
3. Assert `correlationId` as present, non-empty, and matching the DMS correlation-id format. Do not make integration or E2E tests inject a fixed correlation id just to compare the entire body byte-for-byte. Response-shape tests should compare all stable ProblemDetails members exactly and treat `correlationId` as a validated dynamic field.
4. Yes. Slice 6 should avoid adding dedicated tests for `auth.md` §2.4 custom-view/no-EdOrg-claims wording, because custom view ProblemDetails are outside this story. If a shared formatter already has narrow unit coverage that must be preserved while changing relationship code, keep that coverage; leave CRUD custom-view wire assertions to the view-based authorization story.
5. Emit a single aggregated no-relationship `errors` entry. Aggregate the selected failed OR-strategy/subject readable securable names in deterministic configured strategy/subject/contributor order, de-duplicate by displayed readable name, and format the EdOrg claim text once. Preserve per-strategy and per-subject identity internally for DTO assertions and hint ordering; do not emit one no-relationship error per failed strategy or subject.
