# InsertReferences Integration SQL Tests

These manual SQL scripts exercise the `dms.InsertReferences` Postgres function across common scenarios. Each script:

- Enables `ON_ERROR_STOP` to fail fast.
- Wraps commands in `BEGIN … ROLLBACK` so the database is unchanged after execution.
- Emits descriptive `\echo` headers to make terminal output self-documenting.

## Prerequisites

- `uuid-ossp` extension installed in the target database (`CREATE EXTENSION IF NOT EXISTS "uuid-ossp";`).
- Appropriate connection credentials (default scripts assume `postgres` superuser).
- A shell with `psql` available and access to the DMS database.

## Running the suite

From the repository root:

```bash
export PGPASSWORD="<password>"
PGPASSWORD="$PGPASSWORD" psql \
  --host=localhost --port=5432 --username=postgres --dbname=edfi_datamanagementservice \
  --file=src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/sql/InsertReferences/<script>.sql
```

Run each script individually in numeric order (`01_*.sql` through `05_*.sql`). The scripts print key results at the end of each test block.

## Script overview & expected outcomes

1. **01_insert_valid.sql** – Inserts one parent and one resolved alias. Expect `insert_result = t` and one reference row for the parent/partition.
2. **02_update_existing_reference.sql** – After initial insert, reassigns the alias to a different document and reruns the function. Expect both calls to return `t` and the stored reference to point to the new document (partition key `6`).
3. **03_deduplicate_duplicates.sql** – Supplies the same referential ID twice. Expect `result = t` and the final reference count to be `1`, demonstrating deduplication.
4. **04_partial_success.sql** – Mixes one valid and one missing alias. Expect `result = f`, the valid reference persisted, and `temp_reference_stage` to contain the missing referential ID with `aliasid` NULL.
5. **05_delete_missing_references.sql** – Initial call registers two references; second call supplies only one. Expect `initial_result = t` with count `2`, `pruned_result = t`, and the remaining reference list containing only the kept alias.

Each script finishes with `ROLLBACK`, so repeat runs are idempotent.
