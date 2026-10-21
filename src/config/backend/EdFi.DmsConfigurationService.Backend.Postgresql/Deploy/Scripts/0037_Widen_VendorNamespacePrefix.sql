-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1626: raises the vendor namespace prefix limit from 128 to 255 characters, matching the
-- ODS/API, the ODS Admin API and the Ed-Fi API namespace columns.
--
-- Every database, new or existing, runs this script; 0001_Create_Vendor_Table still creates the
-- column at 128. The guard only widens: a column an operator already widened past 255 is left
-- alone, never shrunk.
--
-- The change is in place. PostgreSQL widens a varchar column without rewriting the table or its
-- primary key (VendorId, NamespacePrefix), and the column stays NOT NULL. No row can fail the
-- change, because every stored value already fits the narrower column.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name = 'VendorNamespacePrefix'
          AND column_name = 'NamespacePrefix'
          AND character_maximum_length < 255
    ) THEN
        ALTER TABLE "dmscs"."VendorNamespacePrefix"
            ALTER COLUMN "NamespacePrefix" TYPE VARCHAR(255);
    END IF;
END$$;
