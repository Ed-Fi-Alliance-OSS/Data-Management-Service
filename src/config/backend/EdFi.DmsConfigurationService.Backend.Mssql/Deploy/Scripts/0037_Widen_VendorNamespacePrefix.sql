-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1626: raises the vendor namespace prefix limit from 128 to 255 characters, matching the
-- ODS/API, the ODS Admin API and the Ed-Fi API namespace columns.
--
-- Every database, new or existing, runs this script; 0001_Create_Vendor_Table still creates the
-- column at 128. The guard widens only an nvarchar column shorter than 255 characters, so a
-- column an operator already widened or changed to another type is left alone.
--
-- The statement restates the column's current collation. ALTER COLUMN without COLLATE applies the
-- database default, and SQL Server refuses to change the collation of a column the primary key
-- (VendorId, NamespacePrefix) depends on (errors 5074 and 4922), so a column whose collation
-- differs from the database default would otherwise block the upgrade. The collation name comes
-- from sys.columns, not from input. With it restated, SQL Server widens the column in place and
-- keeps the primary key. The statement also restates NOT NULL. No row can fail the change,
-- because every stored value already fits the narrower column.
DECLARE @collation SYSNAME;

SELECT @collation = c.collation_name
FROM sys.columns c
WHERE c.object_id = OBJECT_ID(N'dmscs.VendorNamespacePrefix')
  AND c.name = N'NamespacePrefix'
  AND TYPE_NAME(c.user_type_id) = N'nvarchar'
  AND c.max_length BETWEEN 1 AND 509;

IF @collation IS NOT NULL
BEGIN
    DECLARE @widen NVARCHAR(MAX) =
        N'ALTER TABLE dmscs.VendorNamespacePrefix ALTER COLUMN NamespacePrefix NVARCHAR(255) COLLATE '
        + @collation + N' NOT NULL;';
    EXEC sys.sp_executesql @widen;
END;
