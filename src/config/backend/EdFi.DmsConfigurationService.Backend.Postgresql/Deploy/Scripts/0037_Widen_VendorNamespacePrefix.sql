-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1626: raises the vendor namespace prefix limit from 128 to 255 characters, matching the
-- ODS/API, the ODS Admin API and the Ed-Fi API namespace columns.
--
-- Every database, new or existing, runs this script; 0001_Create_Vendor_Table still creates the
-- column at 128. The guard widens only a character varying column shorter than 255, so a column
-- an operator already widened or changed to another type is left alone.
--
-- The statement restates any non-default collation the column has, because ALTER COLUMN ... TYPE
-- without COLLATE gives the column the default collation and rebuilds the primary key
-- (VendorId, NamespacePrefix). With the collation unchanged, PostgreSQL widens the column in
-- place, without rewriting the table or rebuilding the primary key, and the column stays NOT NULL.
-- No row can fail the change, because every stored value already fits the narrower column.
DO $$
DECLARE
    collation_clause TEXT;
BEGIN
    SELECT CASE
               WHEN collation_name IS NULL THEN ''
               ELSE format(' COLLATE %I.%I', collation_schema, collation_name)
           END
    INTO collation_clause
    FROM information_schema.columns
    WHERE table_schema = 'dmscs'
      AND table_name = 'VendorNamespacePrefix'
      AND column_name = 'NamespacePrefix'
      AND data_type = 'character varying'
      AND character_maximum_length < 255;

    IF FOUND THEN
        EXECUTE 'ALTER TABLE "dmscs"."VendorNamespacePrefix" ALTER COLUMN "NamespacePrefix" TYPE VARCHAR(255)'
            || collation_clause;
    END IF;
END$$;
