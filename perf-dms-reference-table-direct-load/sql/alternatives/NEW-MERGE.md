Why ON CONFLICT helps
  Once each (parentdocumentpartitionkey, parentdocumentid, aliasid) combination is unique, INSERT … ON CONFLICT … DO UPDATE lets us refresh rows without the extra update statement in the
  differential prototype. We still need a post-insert DELETE to remove references that disappeared from the payload, but inserts/updates collapse into one statement and reduce FK trigger
  work. Because dms.reference is list-partitioned, PostgreSQL requires the supporting unique index to exist on every leaf partition (global unique indexes aren’t available yet).

  ———

  1. Add a parent+alias unique index (online migration)

  -- run once during a maintenance window
  DO $$
  DECLARE
      partition regclass;
  BEGIN
      FOR partition IN
          SELECT inhrelid
          FROM pg_inherits
          WHERE inhparent = 'dms.reference'::regclass
      LOOP
          EXECUTE format(
              'CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS %I_parent_alias_uix
                 ON %s (parentdocumentpartitionkey, parentdocumentid, aliasid);',
              partition::text,
              partition
          );
      END LOOP;
  END;
  $$;

  -- define parent-level constraint so future partitions inherit the rule
  ALTER TABLE ONLY dms.reference
      ADD CONSTRAINT reference_parent_alias_unique
      UNIQUE USING INDEX reference_00_parent_alias_uix; -- pick any existing partition index

  (Choose the first partition’s index as the template; subsequent partitions inherit the constraint automatically.)

  ———

  2. ON CONFLICT function using the temp staging table

  CREATE OR REPLACE FUNCTION dms.InsertReferences_OnConflict(
      parent_document_id bigint,
      parent_document_partition_key smallint,
      referential_ids uuid[],
      referential_partition_keys smallint[]
  ) RETURNS boolean
  LANGUAGE plpgsql
  AS $$
  DECLARE
      invalid boolean;
  BEGIN
      CREATE TEMP TABLE IF NOT EXISTS dms.temp_reference_stage
      (
          parentdocumentid               bigint,
          parentdocumentpartitionkey     smallint,
          referentialpartitionkey        smallint,
          referentialid                  uuid,
          aliasid                        bigint,
          referenceddocumentid           bigint,
          referenceddocumentpartitionkey smallint
      ) ON COMMIT PRESERVE ROWS;

      TRUNCATE dms.temp_reference_stage;

      INSERT INTO dms.temp_reference_stage
      SELECT
          parent_document_id,
          parent_document_partition_key,
          ids.referentialpartitionkey,
          ids.referentialid,
          a.id,
          a.documentid,
          a.documentpartitionkey
      FROM unnest(referential_ids, referential_partition_keys)
              AS ids(referentialid, referentialpartitionkey)
      LEFT JOIN dms.alias a
             ON a.referentialid = ids.referentialid
            AND a.referentialpartitionkey = ids.referentialpartitionkey;

      WITH upsert AS (
          INSERT INTO dms.reference (
              parentdocumentid,
              parentdocumentpartitionkey,
              aliasid,
              referentialpartitionkey,
              referenceddocumentid,
              referenceddocumentpartitionkey
          )
          SELECT
              parentdocumentid,
              parentdocumentpartitionkey,
              aliasid,
              referentialpartitionkey,
              referenceddocumentid,
              referenceddocumentpartitionkey
          FROM dms.temp_reference_stage
          WHERE aliasid IS NOT NULL
          ON CONFLICT ON CONSTRAINT reference_parent_alias_unique
          DO UPDATE
             SET referenceddocumentid = EXCLUDED.referenceddocumentid,
                 referenceddocumentpartitionkey = EXCLUDED.referenceddocumentpartitionkey
             WHERE (dms.reference.referenceddocumentid,
                    dms.reference.referenceddocumentpartitionkey)
                 IS DISTINCT FROM
                   (EXCLUDED.referenceddocumentid,
                    EXCLUDED.referenceddocumentpartitionkey)
          RETURNING 1
      )
      DELETE FROM dms.reference r
      WHERE r.parentdocumentid = parent_document_id
        AND r.parentdocumentpartitionkey = parent_document_partition_key
        AND NOT EXISTS (
              SELECT 1
              FROM dms.temp_reference_stage s
              WHERE s.aliasid = r.aliasid
                AND s.referentialpartitionkey = r.referentialpartitionkey
        );

      invalid :=
          EXISTS (
              SELECT 1
              FROM dms.temp_reference_stage
              WHERE aliasid IS NULL
          );

      RETURN NOT invalid;
  END;
  $$;

  - Inserts and updates happen in a single statement; FK triggers run only once per reference.
  - The DELETE removes references no longer present in the payload.
  - The function returns true when every referential ID resolved; callers that need the exact failures can query the temp table (e.g., SELECT referentialid FROM dms.temp_reference_stage
    WHERE aliasid IS NULL;) or a dedicated logging table if cross-transaction visibility is required.

  ———

  3. C# caller changes (single parent, boolean result)
  src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:512

  await using var command = new NpgsqlCommand(
      "SELECT dms.InsertReferences_OnConflict($1, $2, $3, $4)",
      connection,
      transaction)
  {
      Parameters =
      {
          new() { Value = bulkReferences.ParentDocumentId },
          new() { Value = bulkReferences.ParentDocumentPartitionKey },
          new() { Value = bulkReferences.ReferentialIds },
          new() { Value = bulkReferences.ReferentialPartitionKeys }
      }
  };

  var success = (bool)await command.ExecuteScalarAsync();
  if (!success)
  {
      // Option A: query the session temp table immediately
      await using var detail = new NpgsqlCommand(
          "SELECT referentialid FROM dms.temp_reference_stage WHERE aliasid IS NULL",
          connection,
          transaction);
      await using var reader = await detail.ExecuteReaderAsync();
      // ... collect GUIDs and raise application error
  }

  (If you prefer a temp table per session, keep ON COMMIT PRESERVE ROWS; if you want it scoped to a single transaction use ON COMMIT DELETE ROWS and fetch the failures before commit.)

  ———

  4. Optional enhancements

  1. Functional variant for invalid IDs – create dms.FetchInvalidReferences() that reads failures from the staging table or a logging table keyed on (parent_document_id,
     parent_partition_key, txid_current()).
  2. Prepared statements – call command.Prepare() after the first execution per connection to lock in the plan; the function now has stable parameter types, so reuse works well.
  3. Batch inserts – for very large payloads, populate temp_reference_stage via COPY using NpgsqlBinaryImporter before calling the function to avoid transmitting long arrays.
  4. Auto-vacuum relief – once the ON CONFLICT path is live, monitor n_dead_tup on reference_* partitions; with fewer deletes the autovacuum frequency can be dialed back (raise
     autovacuum_vacuum_scale_factor per partition if necessary).

  This ON CONFLICT approach keeps the algorithm “insert-or-update + delete what’s gone,” removing the extra update statement and shaving per-call WAL while still protecting referential
  integrity. 
