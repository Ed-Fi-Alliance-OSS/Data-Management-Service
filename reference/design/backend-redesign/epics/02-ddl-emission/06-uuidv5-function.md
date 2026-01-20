# Story: Engine UUIDv5 Helper Function (PostgreSQL + SQL Server)

## Description

Implement UUIDv5 (RFC 4122) generation inside each supported database engine so the database can compute `ReferentialId` deterministically from identity projection values:

- required for trigger-driven maintenance of `dms.ReferentialIdentity` on identity changes/cascades, and
- required for cross-engine equivalence and Core-vs-DB parity testing.

The database implementation must match DMS Core’s UUIDv5 behavior byte-for-byte (same namespace GUID and same input byte sequence/encoding), per:

- `src/dms/core/EdFi.DataManagementService.Core/Extraction/ReferentialIdCalculator.cs` (source of truth for Core behavior)
- `reference/design/backend-redesign/referential-identity-test-plan.md` (parity + cross-engine equivalence)

## Acceptance Criteria

- DDL generation emits a deterministic UUIDv5 helper in schema `dms` for:
  - PostgreSQL (returns `uuid`)
  - SQL Server (returns `uniqueidentifier`)
- The helper:
  - accepts a namespace UUID and a name string (or equivalent byte input),
  - produces RFC 4122 UUIDv5 output,
  - is stable for the same inputs, and
  - matches DMS Core output exactly for the same `(namespace, name)` inputs.
- Parity tests demonstrate:
  - Core-computed UUIDv5 == DB-computed UUIDv5 (PG + SQL Server), and
  - PG-computed UUIDv5 == SQL Server-computed UUIDv5 for representative fixtures (encoding/collation edge cases included).
- The chosen implementation strategy is explicit and repeatable in provisioning:
  - either relies only on built-in engine capabilities, or
  - provisions any required extensions/features as part of the generated DDL/provision workflow.
- DDL ordering ensures the helper exists before any triggers/functions that call it (e.g., `dms.ReferentialIdentity` maintenance triggers).

## Tasks

1. Decide the engine strategy (built-in vs extension vs custom SQL) and document the choice (including required privileges/availability).
2. Implement PostgreSQL UUIDv5 helper emission and add fixture-based validation.
3. Implement SQL Server UUIDv5 helper emission and add fixture-based validation (encoding/collation explicitly controlled to match Core).
4. Add parity/cross-engine equivalence tests (Core ↔ PG, Core ↔ SQL Server, PG ↔ SQL Server) per `referential-identity-test-plan.md`.
5. Ensure generated DDL statement ordering creates the helper before any dependent programmable objects.
