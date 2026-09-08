-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1430: "ExpirationDate" held an instant in a wall-clock column. Npgsql sends DateTimeOffset
-- as timestamptz, so the insert converted down through the session time zone and the cleanup
-- predicate converted the stored column back up through it. On a DST-observing server those two
-- conversions do not round-trip, which made the cleanup sweep's view of a token's expiry depend on
-- the server's time zone. Storing the instant directly removes the conversion from both paths.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'OpenIddictToken'
          AND column_name = 'ExpirationDate'
          AND data_type = 'timestamp without time zone'
    ) THEN
        -- No USING clause, deliberately. The implicit assignment cast reinterprets each stored
        -- wall clock through the session time zone this script runs under. That is the best
        -- available inverse of how Npgsql wrote the rows, but it is not a full repair: the old
        -- column type discarded information before this script ever runs. Three cases, and they
        -- differ in whether the result can land early:
        --
        --   1. Same session zone as the write, unambiguous wall clock. Exact recovery.
        --
        --   2. Same session zone, wall clock in a DST transition window. PostgreSQL's own
        --      resolution applies: a fall-back wall clock is ambiguous, because two instants an
        --      hour apart were stored identically, and resolves to the later of the two; a
        --      spring-forward gap wall clock resolves forward. Both land later than or equal to
        --      the true instant, so cleanup errs toward retaining the row.
        --
        --   3. Session zone changed since the write. Irrecoverable, and the result may shift
        --      EARLIER as well as later, depending on the two offsets. A row written under
        --      America/New_York for 18:00Z stores 14:00; migrated under UTC it reconstructs as
        --      14:00Z, four hours early. Nothing here can detect or correct that, because the
        --      zone the row was written under was never recorded. Where it matters, run the
        --      upgrade under the same session time zone the rows were written under; on the
        --      shipped UTC containers that is automatic and case 1 applies throughout.
        --
        -- `USING "ExpirationDate" AT TIME ZONE 'UTC'` looks equivalent and is not. It forces case
        -- 3 on every row of a non-UTC server, reading each stored wall clock as though it were
        -- already UTC and shifting it by the server's offset.
        ALTER TABLE "dmscs"."OpenIddictToken"
            ALTER COLUMN "ExpirationDate" TYPE timestamp with time zone;
    END IF;
END$$;
