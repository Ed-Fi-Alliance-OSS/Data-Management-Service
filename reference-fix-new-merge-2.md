# InsertReferences Remediation Plan

This document captures the follow-up work requested by the DBA after reviewing the `auto_explain` output for `dms.InsertReferences`. It focuses on correctness, concurrency, and write-amplification fixes.

## 1. Fail Fast On Invalid Aliases

### Goals
- Prevent the function from deleting existing references when any staged alias is unresolved.
- Reduce WAL churn for rejected requests.

### Steps
1. Populate the staging table as today, but run the invalid-alias check **before** the `WITH upsert` CTE.
2. If any staged row has `aliasid IS NULL`, immediately `RETURN FALSE`.
3. Skip the upsert and delete logic in that execution path.

### Code Sketch
```sql
WITH staged AS (...)
INSERT INTO temp_reference_stage
SELECT ...;

IF EXISTS (SELECT 1 FROM temp_reference_stage WHERE aliasid IS NULL) THEN
    RETURN FALSE;
END IF;

-- proceed with upsert/delete only when all aliases resolved
```

### Validation
- Add an integration test covering an invalid referential ID. The test should confirm:
  - No rows are inserted or deleted from `dms.Reference`.
  - The function returns `FALSE`.
- Re-run `auto_explain` to confirm the delete CTE is not invoked in this path.

## 2. Reduce Locking Overhead

### Goals
- Eliminate redundant `SELECT ... FOR KEY SHARE` probes that add latency and FPIs.
- Rely on `INSERT ... ON CONFLICT` for concurrency whenever possible.

### Steps
1. Audit `SqlAction.InsertReferences` and companion helpers for explicit lock statements.
2. Determine whether each lock protects behavior the `ON CONFLICT` clause already covers.
3. Remove redundant locks, or batch them into a single query per parent document when they remain necessary.

### Code Sketch
```csharp
// Before
await using var lockCommand = new NpgsqlCommand(
    "SELECT 1 FROM dms.Document WHERE Id = $1 FOR KEY SHARE",
    connection,
    transaction);
...

// After (when safe)
// No explicit lock; rely on ON CONFLICT for concurrency guarantees.
```

### Validation
- Verify that high-concurrency scenarios still behave correctly by running targeted integration tests.
- Confirm the lock steps disappear from `auto_explain` output.

## 3. Targeted Reference Cleanup

### Goals
- Replace the full anti-join delete with a differential delete.
- Reduce buffer scans and WAL when only a subset of aliases change.

### Steps
1. Rewrite the cleanup query to delete only references absent from the staging set.
2. Use `DELETE ... USING temp_reference_stage` keyed by `(aliasId, referentialPartitionKey)`.
3. Ensure the query uses indexes to limit scans to the parent’s partition.

### Code Sketch
```sql
DELETE FROM dms.Reference r
USING temp_reference_stage s
WHERE r.ParentDocumentId = p_parentDocumentId
  AND r.ParentDocumentPartitionKey = p_parentDocumentPartitionKey
  AND r.AliasId = s.AliasId
  AND r.ReferentialPartitionKey = s.ReferentialPartitionKey
  AND s.is_present = FALSE;
```

One option is to add a marker column (`is_present`) while constructing the staging table, so the delete only fires for rows explicitly missing from the request.

### Validation
- Add tests that exercise scenarios where:
  - No aliases change (no deletes).
  - One alias changes (only that row updates).
- Measure reduced WAL/buffer usage via `auto_explain`.

## 4. Persistent Staging Structure

### Goals
- Avoid re-creating and truncating `temp_reference_stage` on each pooled connection request.
- Reduce local writes and partition-pruning overhead.

### Steps
1. Move the `CREATE TEMP TABLE` call into a session-init routine (e.g., the connection pool warm-up path).
2. Switch to `UNLOGGED` or global temporary tables if session reuse is guaranteed.
3. Replace `TRUNCATE` with `DELETE` or `INSERT ... SELECT` into a session-owned table variable when appropriate.

### Code Sketch
```sql
-- Session bootstrap
CREATE TEMP TABLE IF NOT EXISTS temp_reference_stage (
    ...
) ON COMMIT PRESERVE ROWS;

-- Per-call logic
DELETE FROM temp_reference_stage;
INSERT INTO temp_reference_stage SELECT ...;
```

### Validation
- Ensure pooled connections reuse the table correctly without leaking data between requests.
- Monitor local buffer writes and confirm they drop after the change.

## 5. Regression & Observability

### Goals
- Guarantee behavior parity while the internals change.
- Provide proof of improved performance to operations.

### Steps
1. Extend integration tests to cover the new code paths described above.
2. Capture new `auto_explain` samples (same settings) after each major change.
3. Update runbooks describing how to inspect staging tables and log output for debugging.

### Acceptance Criteria
- Invalid referential IDs leave the parent’s references untouched.
- Typical upserts avoid full-table deletes and show materially smaller WAL volumes.
- Locking overhead decreases (no per-call key-share probes in the plans).
- The staging table persists across pooled connections without re-creation.
```
