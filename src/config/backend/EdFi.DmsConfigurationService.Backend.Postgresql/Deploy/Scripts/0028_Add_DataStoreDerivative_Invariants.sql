-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Enforce at most one derivative of each type per data store, and restrict DerivativeType to
-- exactly 'Snapshot' or 'ReadReplica'. Both constraints are added WITH CHECK, so legacy rows that
-- violate either one would otherwise fail the upgrade with a bare constraint-violation error. The
-- preflight below runs first and stops the upgrade with the offending tuples and remediation
-- guidance instead. It never deletes a row, rewrites a type, or chooses among duplicates.

-- The allowed-value test states its comparison semantics rather than inheriting the database or
-- column collation: "C" is a built-in deterministic collation, so equality under it is ordinal, and
-- octet_length makes the stored-length contract explicit so trailing whitespace is rejected. The
-- invalid-type preflight below negates this same expression, so a value the check constraint would
-- reject is always diagnosed by the preflight first.
-- The thrown error carries only a bounded sample, because SQL Server's THROW delivers at most 2047
-- characters and the two engines keep one diagnostic contract. These two statements carry the
-- complete offender sets instead. DbUp's LogScriptOutput writes every result set a script returns
-- to the deployment log, and it does so as the reader is consumed, so rows emitted here reach the
-- operator even though the statement that follows aborts the upgrade. A RAISE NOTICE would not:
-- notices are not part of a result set and never reach the DbUp log. On a conforming database both
-- statements return no rows. Ordering is deterministic and the tuple shapes match the thrown
-- diagnostics exactly.
SELECT format('(%s, %s, %s)', candidate."DataStoreId", candidate."DerivativeType", candidate."Id")
    AS "DuplicateDataStoreDerivative"
FROM "dmscs"."DataStoreDerivative" candidate
WHERE EXISTS (
    SELECT 1
    FROM "dmscs"."DataStoreDerivative" other
    WHERE other."DataStoreId" = candidate."DataStoreId"
      AND other."DerivativeType" = candidate."DerivativeType"
      AND other."Id" <> candidate."Id"
)
ORDER BY candidate."DataStoreId", candidate."DerivativeType", candidate."Id";

SELECT format('(%s, %s, ''%s'')', "Id", "DataStoreId", "DerivativeType")
    AS "InvalidDataStoreDerivativeType"
FROM "dmscs"."DataStoreDerivative"
WHERE NOT (
    ("DerivativeType" COLLATE "C" = 'Snapshot' AND octet_length("DerivativeType") = 8)
    OR ("DerivativeType" COLLATE "C" = 'ReadReplica' AND octet_length("DerivativeType") = 11)
)
ORDER BY "Id";

DO $$
DECLARE
    message_limit CONSTANT INT := 2048;
    marker_reserve CONSTANT INT := 30;
    remediation CONSTANT TEXT :=
        'Correct an invalid DerivativeType with PUT /v3/dataStoreDerivatives/{id}, or remove an unwanted row with DELETE /v3/dataStoreDerivatives/{id}, then retry the upgrade. Allowed values are exactly ''Snapshot'' and ''ReadReplica''.';
    duplicate_total INT;
    invalid_total INT;
    duplicate_shown INT := 0;
    invalid_shown INT := 0;
    duplicate_list TEXT;
    invalid_list TEXT;
    duplicate_header TEXT;
    invalid_header TEXT;
    conditions INT;
    per_condition INT;
    message_text TEXT := '';
BEGIN
    -- Duplicate detection deliberately uses the plain columns, which is the equality the unique
    -- constraint itself will apply. Forcing a binary comparison here would make the preflight less
    -- predictive than the constraint it protects.
    SELECT count(*)
    INTO duplicate_total
    FROM "dmscs"."DataStoreDerivative" candidate
    WHERE EXISTS (
        SELECT 1
        FROM "dmscs"."DataStoreDerivative" other
        WHERE other."DataStoreId" = candidate."DataStoreId"
          AND other."DerivativeType" = candidate."DerivativeType"
          AND other."Id" <> candidate."Id"
    );

    SELECT count(*)
    INTO invalid_total
    FROM "dmscs"."DataStoreDerivative"
    WHERE NOT (
        ("DerivativeType" COLLATE "C" = 'Snapshot' AND octet_length("DerivativeType") = 8)
        OR ("DerivativeType" COLLATE "C" = 'ReadReplica' AND octet_length("DerivativeType") = 11)
    );

    IF duplicate_total = 0 AND invalid_total = 0 THEN
        RETURN;
    END IF;

    duplicate_header := format(
        'DataStoreDerivative upgrade blocked: %s duplicate (DataStoreId, DerivativeType) row(s): ',
        duplicate_total
    );
    invalid_header := format(
        'DataStoreDerivative upgrade blocked: %s row(s) with an invalid DerivativeType: ',
        invalid_total
    );

    -- The tuple budget is computed before either list is built, so the assembled message is within
    -- the limit by construction and is never truncated afterwards.
    conditions := (CASE WHEN duplicate_total > 0 THEN 1 ELSE 0 END)
        + (CASE WHEN invalid_total > 0 THEN 1 ELSE 0 END);
    per_condition := GREATEST(
        (
            message_limit - length(remediation) - 2
            - (CASE WHEN duplicate_total > 0 THEN length(duplicate_header) + marker_reserve ELSE 0 END)
            - (CASE WHEN invalid_total > 0 THEN length(invalid_header) + marker_reserve ELSE 0 END)
        ) / conditions,
        0
    );

    IF duplicate_total > 0 THEN
        WITH duplicate_rows AS (
            SELECT candidate."DataStoreId", candidate."DerivativeType", candidate."Id"
            FROM "dmscs"."DataStoreDerivative" candidate
            WHERE EXISTS (
                SELECT 1
                FROM "dmscs"."DataStoreDerivative" other
                WHERE other."DataStoreId" = candidate."DataStoreId"
                  AND other."DerivativeType" = candidate."DerivativeType"
                  AND other."Id" <> candidate."Id"
            )
        ),
        ordered AS (
            SELECT
                format('(%s, %s, %s)', "DataStoreId", "DerivativeType", "Id") AS tuple_text,
                row_number() OVER (ORDER BY "DataStoreId", "DerivativeType", "Id") AS position
            FROM duplicate_rows
        ),
        measured AS (
            SELECT
                tuple_text,
                position,
                sum(length(tuple_text) + 2) OVER (ORDER BY position ROWS UNBOUNDED PRECEDING) AS used
            FROM ordered
        )
        SELECT string_agg(tuple_text, ', ' ORDER BY position), count(*)
        INTO duplicate_list, duplicate_shown
        FROM measured
        WHERE position <= 20
          AND (position = 1 OR used <= per_condition);

        message_text := message_text || duplicate_header || duplicate_list;
        IF duplicate_total > duplicate_shown THEN
            message_text := message_text || format(', ... and %s more.', duplicate_total - duplicate_shown);
        ELSE
            message_text := message_text || '.';
        END IF;
        message_text := message_text || ' ';
    END IF;

    IF invalid_total > 0 THEN
        WITH invalid_rows AS (
            SELECT "Id", "DataStoreId", "DerivativeType"
            FROM "dmscs"."DataStoreDerivative"
            WHERE NOT (
                ("DerivativeType" COLLATE "C" = 'Snapshot' AND octet_length("DerivativeType") = 8)
                OR ("DerivativeType" COLLATE "C" = 'ReadReplica' AND octet_length("DerivativeType") = 11)
            )
        ),
        ordered AS (
            SELECT
                -- The type value is quoted so trailing whitespace is visible to the operator.
                format('(%s, %s, ''%s'')', "Id", "DataStoreId", "DerivativeType") AS tuple_text,
                row_number() OVER (ORDER BY "Id") AS position
            FROM invalid_rows
        ),
        measured AS (
            SELECT
                tuple_text,
                position,
                sum(length(tuple_text) + 2) OVER (ORDER BY position ROWS UNBOUNDED PRECEDING) AS used
            FROM ordered
        )
        SELECT string_agg(tuple_text, ', ' ORDER BY position), count(*)
        INTO invalid_list, invalid_shown
        FROM measured
        WHERE position <= 20
          AND (position = 1 OR used <= per_condition);

        message_text := message_text || invalid_header || invalid_list;
        IF invalid_total > invalid_shown THEN
            message_text := message_text || format(', ... and %s more.', invalid_total - invalid_shown);
        ELSE
            message_text := message_text || '.';
        END IF;
        message_text := message_text || ' ';
    END IF;

    RAISE EXCEPTION '%', message_text || remediation;
END$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_DataStoreDerivative_DataStoreId_DerivativeType'
          AND conrelid = '"dmscs"."DataStoreDerivative"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."DataStoreDerivative"
            ADD CONSTRAINT "UX_DataStoreDerivative_DataStoreId_DerivativeType" UNIQUE ("DataStoreId", "DerivativeType");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_DataStoreDerivative_DerivativeType'
          AND conrelid = '"dmscs"."DataStoreDerivative"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."DataStoreDerivative"
            ADD CONSTRAINT "CK_DataStoreDerivative_DerivativeType" CHECK (
                ("DerivativeType" COLLATE "C" = 'Snapshot' AND octet_length("DerivativeType") = 8)
                OR ("DerivativeType" COLLATE "C" = 'ReadReplica' AND octet_length("DerivativeType") = 11)
            );
    END IF;
END$$;

-- The unique constraint's backing index leads with DataStoreId, so it serves the parent lookup and
-- the child-side foreign-key maintenance that this narrower index served. Dropped only after the
-- constraint above is in place.
DROP INDEX IF EXISTS "dmscs"."IX_DataStoreDerivative_DataStoreId";

COMMENT ON CONSTRAINT "UX_DataStoreDerivative_DataStoreId_DerivativeType" ON "dmscs"."DataStoreDerivative" IS
    'Each data store has at most one derivative of each type';

COMMENT ON CONSTRAINT "CK_DataStoreDerivative_DerivativeType" ON "dmscs"."DataStoreDerivative" IS
    'DerivativeType is exactly "Snapshot" or "ReadReplica", compared ordinally including length';
