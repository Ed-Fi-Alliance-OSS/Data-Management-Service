# Story: Read and Cache DB Fingerprint (`dms.EffectiveSchema`)

## Description

Implement the runtime “first use” schema validation step:

- After instance routing selects a database connection string, read the singleton `dms.EffectiveSchema` row and cache it per connection string.
- The read occurs before any schema-dependent work (plan compilation, relational reads/writes).
- Errors are fail-fast per database (a mis-provisioned DB does not prevent serving other DBs).

This follows `reference/design/backend-redesign/transactions-and-concurrency.md` (“Schema Validation (EffectiveSchema)”).

## Acceptance Criteria

- On the first request for a given connection string, DMS reads `dms.EffectiveSchema` and caches:
  - `ApiSchemaFormatVersion`
  - `EffectiveSchemaHash`
  - `ResourceKeyCount`
  - `ResourceKeySeedHash`
- Subsequent requests for the same connection string do not repeat the DB read (cache hit).
- If `dms.EffectiveSchema` is missing or the singleton row is absent, requests fail fast with an actionable error indicating the DB must be provisioned.
- The “effective schema read” runs immediately after instance routing and before any schema-dependent operations.

## Tasks

1. Add a `DatabaseFingerprintProvider` (or equivalent) keyed by connection string with thread-safe caching.
2. Implement dialect-specific SQL to read the singleton `dms.EffectiveSchema` row and validate singleton invariants.
3. Integrate the fingerprint provider into the request pipeline at the earliest safe point after instance routing.
4. Add unit tests for:
   1. cache behavior,
   2. missing table/row behavior,
   3. “no schema-dependent work before fingerprint” guard (pipeline ordering test).

