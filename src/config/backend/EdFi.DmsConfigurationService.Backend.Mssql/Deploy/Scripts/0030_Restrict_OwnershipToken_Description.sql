-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_OwnershipToken_Description_NotBlank' AND parent_object_id = OBJECT_ID('dmscs.OwnershipToken'))
BEGIN
    ALTER TABLE dmscs.OwnershipToken ADD CONSTRAINT CK_OwnershipToken_Description_NotBlank CHECK (
        LEN(
            REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(Description, N' ', N''), NCHAR(9), N''), NCHAR(10), N''), NCHAR(11), N''), NCHAR(12), N''), NCHAR(13), N'')
        ) > 0
    );
END;
