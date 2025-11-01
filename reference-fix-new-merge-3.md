## Plan: Partition-Targeted Reference Cleanup

1. Inspect the existing cleanup delete in `dms.InsertReferences` to confirm it still scans all reference partitions and note how the current SQL is structured.
2. Replace that delete with a dynamic statement that targets only the appropriate `dms.reference_<partition>` child table (using `EXECUTE format(...)`) while keeping the `NOT EXISTS` filter against `temp_reference_stage`.
3. Run the representative `InsertReferences` executions (duplicate IDs, invalid ID, multiple references) and review auto_explain output to ensure the delete now hits a single partition instead of the append/TID scan across all partitions.
