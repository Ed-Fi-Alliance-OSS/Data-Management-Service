# Draft DB Performance Improvement Plan

## Contributors to Slowness
0. PostgreSQL configuration settings:
   - They should be tuned for large table, write-heavy workloads for the server hardware being run on, here an 8-core CPU with 64GB of memory and an SSD.

1. Delete‑all-then‑insert update pattern, per upsert:
   - `dms.InsertReferences` deletes all existing rows for the parent document and reinserts. This generates dead tuples in  indexes and increases WAL volume. Without aggressive per‑partition autovacuum/analyze, bloat accumulates and slows subsequent inserts and scans.

2. Multiple foreign key validations per row:
   - `Reference` validates three FKs: parent `Document`, referenced `Document`, and `Alias` for reference validation. The referenced‑`Document` FK is redundant as the lookup can be achieved by going through `Alias` to `Document`. This extra FK adds a per‑row check and requires an additional index. Additionally, the reference validation FK to `Alias` includes the UUID column `ReferentialId`. This column doesn't need to be on both tables, so could be replaced by the BIGINT `Id` on `Alias`.

3. Join during insert:
   - The `LEFT JOIN` to `Alias` is only used to fill the above mentioned referenced‑`Document` FK (`ReferencedDocumentId`, `ReferencedPartitionKey`). Retaining these columns requires both the join and the referenced‑`Document` FK + index maintenance.

4. Write‑heavy, randomly distributed B‑tree updates:
   - For `Reference` table, each insert maintains: PK, parent index, referenced‑document index, and referential index (for the Alias FK). UUID‑based keys yield randomized inserts that fragment B‑trees and increase page splits unless fillfactor leaves sufficient free space.

5. Partition breadth:
   - With only 16 partitions on the hottest table, per‑partition relation and index sizes are larger, driving more random I/O, higher contention on relation extension, and longer vacuum/maintenance windows.

## Solutions

### PostgreSQL Configuration Tuning

1.  Memory
    - `shared_buffers` - Dedicated memory PostgreSQL allocates. Defaults to 128MB. Set to 16GB for 64GB server. 
    - `effective_cache_size` - A hint to the query planner. Defaults to 4GB. Set to 48GB for 64GB server.
    - `maintenance_work_mem` - For temporary, on-demand allocations. Defaults to 64MB. Set to 2GB for 64GB server.
    - `work_mem` - For temporary, on-demand allocations. Defaults to 4MB. Set to 64MB for 64GB server.

2. Write-Ahead Log
    - `max_wal_size` - Higher values reduce checkpoint I/O bursts. Defaults to 1GB. Set to 8GB for write-heavy workloads
    - `min_wal_size` - Complementary to `max_wal_size`. Defaults to 80MB. Set to 2GB for write-heavy workloads
    - `wal_compression` - Saves disk I/O at a small CPU expense. Defaults to off. Turn on once we get CPU utilization under control

3. Autovacuum
    - `autovacuum_max_workers` - Maximum number of autovacuum processes. Defaults to 3. Set to 4 or 5 to take advantage of more cores.
    - `autovacuum_vacuum_cost_limit` - Cap on the I/O an autovacuum worker can use up. Defaults to 200. Set to 1000 for server with SSD.

### Table Tuning

1. Lower fill factor on all `Reference` indexes
    - Reduce to 75 for write‑hot parent and referenced‑document indexes to leave headroom and reduce page splits.
    - Apply via `ALTER INDEX SET (fillfactor=75);` per partitioned index.

2. Lower fill factor on `Document` and `Alias` indexes with UUID columns to 75 as well.

3. More aggressive autovacuum for `Reference` partitions
    - Lower `autovacuum_vacuum_scale_factor` to 0.02 and `autovacuum_analyze_scale_factor` to 0.01 on each `dms.reference_*` child to keep up with delete-all‑then‑insert churn on a large table.

### Schema Refinements

These changes require DDL updates and limited code changes but are straightforward and should provide larger write‑path improvements.

1. Drop referenced‑document columns and FK from `Reference`
   - Columns to remove: `ReferencedDocumentId`, `ReferencedDocumentPartitionKey`.
   - Drop FK: `FK_Reference_ReferencedDocument`.
   - Drop cross-partition index: `UX_Reference_ReferencedDocumentId (ReferencedDocumentPartitionKey, ReferencedDocumentId)`.
   - Update `dms.InsertReferences` to stop joining to `Alias`; insert only `(ParentDocumentId, ParentDocumentPartitionKey, ReferentialPartitionKey, ReferentialId)`.
   - Update read paths to resolve reverse lookups via `Reference → Alias → Document`:
     - Replace queries at `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:784` and `:826` accordingly.
   - Net effect: remove one FK validation, one write‑heavy index, and the join from the hot insert path.

2. Store `AliasId` instead of `ReferentialId` in `Reference`, simplifying the reference validation FK

   - Replace `(ReferentialPartitionKey, ReferentialId)` with `(ReferentialPartitionKey, AliasId)` referencing the `Alias` PK. Narrower keys reduce row and index width and speed FK lookups.
   - Requires minor changes to insert and read logic and an index on `(ReferentialPartitionKey, AliasId)`.

3. Increase partition breadth for `Reference`, the largest DMS table

   - Expand from 16 to 32/64 partitions to lower per‑partition index sizes, improve cache locality, and reduce contention.

### Switch to UUIDv7 for DocumentUUID

 - Rather than change the fillfactor, we can switch DocumentUUID to be UUIDv7 and thus sequential. https://github.com/sdrapkin/SecurityDriven.FastGuid has this with its `FastGuid.NewPostgreSqlGuid()` function. We would change the partitionkey for Alias to be DocumentPartitionKey.

### Rewrite InsertReferences() to Use INSERT ON CONFLICT UPDATE

- Replace delete-all‑then‑insert pattern with a staged table, set difference delete (existing - staged) plus insert-or-update for the new set, using a new unique constraint on `(ParentDocumentId, ParentDocumentPartitionKey, ReferentialId, ReferentialPartitionKey)` for conflict detection along with INSERT ON CONFLICT UPDATE. (Changes a bit after `AliasId` change above, but same idea applies.)

### Other

- Consider removing replication (`REPLICA IDENTITY FULL`) from Document table to observe cost of supporting streaming via Debezium
