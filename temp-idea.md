 Me:
 
 The expected workload will either be all inserts that don't conflict, or mostly unchanged rows on an update. Duplicates will be rare. The caller sometimes knows that it will be mostly
  unchanged rows. How about an optional parameter (e.g., expect_update boolean DEFAULT false) so the happy-path insert jobs stay on the original ON CONFLICT statement, while the “mostly
  unchanged” jobs opt into MERGE for the lower conflict cost.

DBA:

  That’s a reasonable compromise, with a couple of details to watch:

  - Keep the current INSERT … ON CONFLICT path as the default branch when expect_update = false. That preserves the fast all-insert workload—the insert plan is already inside a cheap
    EXECUTE format(...) against the partition, so nothing changes there.
  - When expect_update = true, switch to the merge-style logic so we can skip the conflict machinery for unchanged rows. Because the partition name is dynamic, you’ll still need EXECUTE
    format(...) in that branch; the MERGE (or split UPDATE/INSERT sequence) can live entirely inside the IF expect_update THEN … block.

    MERGE variant (PG 15+)

    IF expect_update THEN
        EXECUTE format($sql$
            MERGE INTO dms.%I AS target
            USING temp_reference_stage s
               ON target.parentdocumentpartitionkey = $2
              AND target.parentdocumentid          = $1
              AND target.aliasid                   = s.aliasid
            WHEN MATCHED AND (
                  target.referentialpartitionkey        IS DISTINCT FROM s.referentialpartitionkey OR
                  target.referenceddocumentid           IS DISTINCT FROM s.referenceddocumentid OR
                  target.referenceddocumentpartitionkey IS DISTINCT FROM s.referenceddocumentpartitionkey
                )
              THEN UPDATE SET
                   referentialpartitionkey        = s.referentialpartitionkey,
                   referenceddocumentid           = s.referenceddocumentid,
                   referenceddocumentpartitionkey = s.referenceddocumentpartitionkey
            WHEN NOT MATCHED AND s.aliasid IS NOT NULL
              THEN INSERT (parentdocumentid, parentdocumentpartitionkey,
                           aliasid, referentialpartitionkey,
                           referenceddocumentid, referenceddocumentpartitionkey)
                   VALUES (s.parentdocumentid, s.parentdocumentpartitionkey,
                           s.aliasid, s.referentialpartitionkey,
                           s.referenceddocumentid, s.referenceddocumentpartitionkey)
            WHEN MATCHED THEN DO NOTHING
        $sql$, reference_partition)
        USING p_parentDocumentId, p_parentDocumentPartitionKey;
    ELSE
        -- existing INSERT … ON CONFLICT path
    END IF;

    The last WHEN MATCHED THEN DO NOTHING handles the “duplicate but identical payload” case without hitting the conflict clause.

  - Because PL/pgSQL caches a query plan per EXECUTE string, the format literal must differ between branches otherwise you can run into “cached plan must not change result type” errors.
    Using reference_partition keeps the SQL string stable per partition, so caching works fine.
  - Document the semantics clearly for callers: “Set expect_update := true when the batch is mostly replays of existing references; leave it default/false when most rows are new.” If the
    caller has telemetry, they can flip the flag dynamically.
  - Monitor after deployment: the log entry at …log:4880 should drop from ~9 ms to ~3–4 ms when the flag is true and the batch is unchanged. Happy-path insert batches (expect_update =
    false) should remain around the current 7 ms for multi-row cases and sub-3 ms for small ones.

  This gives you the best of both worlds: all-insert workloads stay on the proven conflict path, and update-heavy workloads opt into the merge logic to avoid FK probes and conflict churn.
