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
-- The change is in place. SQL Server widens an NVARCHAR column without dropping or recreating the
-- primary key (VendorId, NamespacePrefix), and the column is restated NOT NULL because ALTER COLUMN
-- would otherwise make it nullable. ALTER COLUMN without COLLATE takes the database default
-- collation, so a column created under a different collation would change; the deploy creates
-- every column under the database default, so none does. No row can fail the change, because every
-- stored value already fits the narrower column.
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dmscs.VendorNamespacePrefix')
      AND name = N'NamespacePrefix'
      AND max_length < 510
)
BEGIN
    ALTER TABLE dmscs.VendorNamespacePrefix ALTER COLUMN NamespacePrefix NVARCHAR(255) NOT NULL;
END;
