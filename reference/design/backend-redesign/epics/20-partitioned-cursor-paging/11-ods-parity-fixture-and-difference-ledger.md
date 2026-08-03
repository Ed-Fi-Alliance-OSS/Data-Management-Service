---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S11: ODS Parity Fixture and Difference Ledger

## Outcome

Provide reproducible ODS 7.3.2 reference infrastructure, the shared contract case definitions, and a
comparison harness that makes the approved DMS differences explicit and detects any unreviewed
cursor or partition contract drift.

## Design References

- [`Compatibility Baseline`](EPIC.md#compatibility-baseline)
- [`Public API Contract`](EPIC.md#public-api-contract)
- [`Test Expectations`](EPIC.md#test-expectations)
- [`Approved Intentional ODS Differences`](EPIC.md#approved-intentional-ods-differences)

## Dependencies

- Hard dependency: E20-S00b for the fixed DMS cursor contract, exact validation precedence, and
  initial approved-difference ledger.
- The fixture may be built in parallel with E20-S01 through E20-S07. E20-S08b consumes it and
  executes the DMS side of every case whose DMS behavior lands after E20-S00b, including the
  response-header cases that require E20-S04 and E20-S05.
- Existing E13 parity and fixture infrastructure is a reusable input, not a substitute for the
  pinned reference API.

## Implementation Scope

- Pin and automate setup/teardown of an ODS 7.3.2 reference API with deterministic configuration,
  authentication, and seeded cursor/partition data.
- Add a reusable runner that sends the same case definitions to configurable ODS and DMS targets,
  captures status/headers/bodies, and normalizes only documented nondeterministic fields.
- Own the case definitions and the captured ODS-side results for validation precedence and exact
  single-error responses, token behavior, traditional and cursor response headers, partition
  validation/boundaries, and published OpenAPI metadata.
- Own comparison evidence for the approved-difference ledger in the epic. Every non-matching case
  must map to a named approved difference; an unmapped difference fails the fixture.
- Retain machine-readable case definitions and results with reference-version and environment
  identity.

## Acceptance Evidence and Test Expectations

- A clean supported environment can start the pinned ODS API, seed it, pass a smoke comparison,
  and tear it down using documented commands.
- The harness implements every case in the epic's
  [`Worked precedence examples`](EPIC.md#worked-precedence-examples) table and asserts each listed
  DMS message and ODS parity/difference outcome, including exactly one error whenever either target
  rejects the request.
- Header cases are defined here and their ODS side is captured here, proving ODS gates the header on
  hydrated body count and emits a token for a non-empty traditional page. The DMS half of that
  comparison, which requires the E20-S04/E20-S05 selected-keyset gate, executes under E20-S08b and
  is not an acceptance item for this story.
- Difference-ledger tests cover DMS message text, stricter parameter rejection, configurable
  defaults, integer bounds, overflow, and decoder behavior without accepting unspecified drift.
- Fixture configuration contains no committed credentials or environment-specific secrets.

## Cross-Provider and Authorization Responsibilities

- The ODS reference provider is pinned as fixture infrastructure. The DMS target is configurable;
  E20-S08b owns cross-provider DMS execution and E20-S08a owns the full authorization matrix.
- Use deterministic non-production principals and seed data. Comparison artifacts must not retain
  access tokens or client secrets.

## Explicit Exclusions / Not Assigned

- DMS functional implementation and broad authorization/E2E scenarios belong to E20-S00a through
  E20-S08b.
- PostgreSQL/SQL Server performance measurement belongs to E20-S09 and E20-S10.
- Production telemetry belongs to E20-S12.
