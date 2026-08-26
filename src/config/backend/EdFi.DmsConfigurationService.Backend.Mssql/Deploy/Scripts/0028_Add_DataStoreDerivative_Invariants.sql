-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Enforce at most one derivative of each type per data store, and restrict DerivativeType to
-- exactly 'Snapshot' or 'ReadReplica'. Both constraints are added WITH CHECK, so legacy rows that
-- violate either one would otherwise fail the upgrade with a bare constraint-violation error. The
-- preflight below runs first and stops the upgrade with the offending tuples and remediation
-- guidance instead. It never deletes a row, rewrites a type, or chooses among duplicates.
--
-- The whole script is a single batch with no GO, so a raised error aborts everything after it.
--
-- The allowed-value test states its comparison semantics rather than inheriting the database or
-- column collation: DerivativeType is NVARCHAR under a typically case-insensitive collation, and
-- SQL Server applies SQL-92 padding to = and IN at any collation, so 'SNAPSHOT' and 'Snapshot '
-- would both pass a naive IN check. Latin1_General_100_BIN2 makes the comparison ordinal and
-- DATALENGTH makes the stored length exact; LEN is unusable because it ignores trailing spaces.
-- The invalid-type preflight below negates this same expression, so a value the check constraint
-- would reject is always diagnosed by the preflight first.

DECLARE @messageLimit INT = 2047;
DECLARE @markerReserve INT = 30;
DECLARE @remediation NVARCHAR(400) =
    N'Correct an invalid DerivativeType with PUT /v3/dataStoreDerivatives/{id}, or remove an unwanted row with DELETE /v3/dataStoreDerivatives/{id}, then retry the upgrade. Allowed values are exactly ''Snapshot'' and ''ReadReplica''.';
DECLARE @duplicateTotal INT;
DECLARE @invalidTotal INT;
DECLARE @duplicateShown INT = 0;
DECLARE @invalidShown INT = 0;
DECLARE @duplicateList NVARCHAR(MAX);
DECLARE @invalidList NVARCHAR(MAX);
DECLARE @duplicateHeader NVARCHAR(200);
DECLARE @invalidHeader NVARCHAR(200);
DECLARE @conditions INT;
DECLARE @perCondition INT;
-- Declared at the thrown limit. That limit is a silent one in both directions: assigning a longer
-- string to a declared NVARCHAR(n) truncates without raising, and THROW itself delivers at most
-- 2047 characters. Nothing reports an over-long message, so the tuple budget computed below is the
-- only thing keeping the thrown text whole, and the result sets emitted below are what guarantee
-- every offender is still reported. Character counts throughout use DATALENGTH/2, never LEN,
-- because LEN ignores trailing spaces and would undercount.
DECLARE @message NVARCHAR(2047) = N'';

-- Duplicate detection deliberately uses the plain columns, which is the equality the unique
-- constraint itself will apply, including the case and padding insensitivity of the column's own
-- collation. Forcing a binary comparison here would make the preflight less predictive than the
-- constraint it protects.
SELECT @duplicateTotal = COUNT(*)
FROM dmscs.DataStoreDerivative candidate
WHERE EXISTS (
    SELECT 1
    FROM dmscs.DataStoreDerivative other
    WHERE other.DataStoreId = candidate.DataStoreId
      AND other.DerivativeType = candidate.DerivativeType
      AND other.Id <> candidate.Id
);

SELECT @invalidTotal = COUNT(*)
FROM dmscs.DataStoreDerivative
WHERE NOT (
    (DerivativeType COLLATE Latin1_General_100_BIN2 = N'Snapshot' AND DATALENGTH(DerivativeType) = 16)
    OR (DerivativeType COLLATE Latin1_General_100_BIN2 = N'ReadReplica' AND DATALENGTH(DerivativeType) = 22)
);

-- The thrown error carries only a bounded sample, because THROW delivers at most 2047 characters.
-- These two statements carry the complete offender sets instead. DbUp's LogScriptOutput writes
-- every result set a script returns to the deployment log, and it does so as the reader is
-- consumed, so rows emitted here reach the operator even though the batch aborts below. On a
-- conforming database both statements return no rows. Ordering is deterministic and the tuple
-- shapes match the thrown diagnostics exactly.
SELECT CONCAT(N'(', candidate.DataStoreId, N', ', candidate.DerivativeType, N', ', candidate.Id, N')')
    AS DuplicateDataStoreDerivative
FROM dmscs.DataStoreDerivative candidate
WHERE EXISTS (
    SELECT 1
    FROM dmscs.DataStoreDerivative other
    WHERE other.DataStoreId = candidate.DataStoreId
      AND other.DerivativeType = candidate.DerivativeType
      AND other.Id <> candidate.Id
)
ORDER BY candidate.DataStoreId, candidate.DerivativeType, candidate.Id;

SELECT CONCAT(N'(', Id, N', ', DataStoreId, N', ''', DerivativeType, N''')')
    AS InvalidDataStoreDerivativeType
FROM dmscs.DataStoreDerivative
WHERE NOT (
    (DerivativeType COLLATE Latin1_General_100_BIN2 = N'Snapshot' AND DATALENGTH(DerivativeType) = 16)
    OR (DerivativeType COLLATE Latin1_General_100_BIN2 = N'ReadReplica' AND DATALENGTH(DerivativeType) = 22)
)
ORDER BY Id;

IF @duplicateTotal > 0 OR @invalidTotal > 0
BEGIN
    SET @duplicateHeader = CONCAT(
        N'DataStoreDerivative upgrade blocked: ',
        @duplicateTotal,
        N' duplicate (DataStoreId, DerivativeType) row(s): '
    );
    SET @invalidHeader = CONCAT(
        N'DataStoreDerivative upgrade blocked: ',
        @invalidTotal,
        N' row(s) with an invalid DerivativeType: '
    );

    -- The tuple budget is computed before either list is built, so the assembled message is within
    -- the limit by construction and is never truncated afterwards.
    SET @conditions = (CASE WHEN @duplicateTotal > 0 THEN 1 ELSE 0 END)
        + (CASE WHEN @invalidTotal > 0 THEN 1 ELSE 0 END);
    SET @perCondition = (
        @messageLimit - DATALENGTH(@remediation) / 2 - 2
        - (CASE WHEN @duplicateTotal > 0 THEN DATALENGTH(@duplicateHeader) / 2 + @markerReserve ELSE 0 END)
        - (CASE WHEN @invalidTotal > 0 THEN DATALENGTH(@invalidHeader) / 2 + @markerReserve ELSE 0 END)
    ) / @conditions;

    IF @perCondition < 0
        SET @perCondition = 0;

    IF @duplicateTotal > 0
    BEGIN
        WITH duplicate_rows AS (
            SELECT candidate.DataStoreId, candidate.DerivativeType, candidate.Id
            FROM dmscs.DataStoreDerivative candidate
            WHERE EXISTS (
                SELECT 1
                FROM dmscs.DataStoreDerivative other
                WHERE other.DataStoreId = candidate.DataStoreId
                  AND other.DerivativeType = candidate.DerivativeType
                  AND other.Id <> candidate.Id
            )
        ),
        ordered AS (
            SELECT
                CAST(
                    CONCAT(N'(', DataStoreId, N', ', DerivativeType, N', ', Id, N')') AS NVARCHAR(MAX)
                ) AS TupleText,
                ROW_NUMBER() OVER (ORDER BY DataStoreId, DerivativeType, Id) AS Position
            FROM duplicate_rows
        ),
        measured AS (
            SELECT
                TupleText,
                Position,
                SUM(DATALENGTH(TupleText) / 2 + 2) OVER (ORDER BY Position ROWS UNBOUNDED PRECEDING) AS Used
            FROM ordered
        )
        SELECT
            @duplicateList = STRING_AGG(TupleText, N', ') WITHIN GROUP (ORDER BY Position),
            @duplicateShown = COUNT(*)
        FROM measured
        WHERE Position <= 20
          AND (Position = 1 OR Used <= @perCondition);

        SET @message = CONCAT(@message, @duplicateHeader, @duplicateList);
        SET @message = @message
            + CASE
                WHEN @duplicateTotal > @duplicateShown
                    THEN CONCAT(N', ... and ', @duplicateTotal - @duplicateShown, N' more.')
                ELSE N'.'
            END
            + N' ';
    END;

    IF @invalidTotal > 0
    BEGIN
        WITH invalid_rows AS (
            SELECT Id, DataStoreId, DerivativeType
            FROM dmscs.DataStoreDerivative
            WHERE NOT (
                (DerivativeType COLLATE Latin1_General_100_BIN2 = N'Snapshot' AND DATALENGTH(DerivativeType) = 16)
                OR (DerivativeType COLLATE Latin1_General_100_BIN2 = N'ReadReplica' AND DATALENGTH(DerivativeType) = 22)
            )
        ),
        ordered AS (
            SELECT
                -- The type value is quoted so trailing whitespace is visible to the operator.
                CAST(
                    CONCAT(N'(', Id, N', ', DataStoreId, N', ''', DerivativeType, N''')') AS NVARCHAR(MAX)
                ) AS TupleText,
                ROW_NUMBER() OVER (ORDER BY Id) AS Position
            FROM invalid_rows
        ),
        measured AS (
            SELECT
                TupleText,
                Position,
                SUM(DATALENGTH(TupleText) / 2 + 2) OVER (ORDER BY Position ROWS UNBOUNDED PRECEDING) AS Used
            FROM ordered
        )
        SELECT
            @invalidList = STRING_AGG(TupleText, N', ') WITHIN GROUP (ORDER BY Position),
            @invalidShown = COUNT(*)
        FROM measured
        WHERE Position <= 20
          AND (Position = 1 OR Used <= @perCondition);

        SET @message = CONCAT(@message, @invalidHeader, @invalidList);
        SET @message = @message
            + CASE
                WHEN @invalidTotal > @invalidShown
                    THEN CONCAT(N', ... and ', @invalidTotal - @invalidShown, N' more.')
                ELSE N'.'
            END
            + N' ';
    END;

    SET @message = @message + @remediation;

    THROW 50000, @message, 1;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.objects
    WHERE name = 'UX_DataStoreDerivative_DataStoreId_DerivativeType'
      AND parent_object_id = OBJECT_ID('dmscs.DataStoreDerivative')
)
    ALTER TABLE dmscs.DataStoreDerivative
        ADD CONSTRAINT UX_DataStoreDerivative_DataStoreId_DerivativeType UNIQUE (DataStoreId, DerivativeType);

IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = 'CK_DataStoreDerivative_DerivativeType'
      AND parent_object_id = OBJECT_ID('dmscs.DataStoreDerivative')
)
    ALTER TABLE dmscs.DataStoreDerivative
        ADD CONSTRAINT CK_DataStoreDerivative_DerivativeType CHECK (
            (DerivativeType COLLATE Latin1_General_100_BIN2 = N'Snapshot' AND DATALENGTH(DerivativeType) = 16)
            OR (DerivativeType COLLATE Latin1_General_100_BIN2 = N'ReadReplica' AND DATALENGTH(DerivativeType) = 22)
        );

-- The unique constraint's backing index leads with DataStoreId, so it serves the parent lookup and
-- the child-side foreign-key maintenance that this narrower index served. Dropped only after the
-- constraint above is in place.
IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_DataStoreDerivative_DataStoreId'
      AND object_id = OBJECT_ID('dmscs.DataStoreDerivative')
)
    DROP INDEX IX_DataStoreDerivative_DataStoreId ON dmscs.DataStoreDerivative;
