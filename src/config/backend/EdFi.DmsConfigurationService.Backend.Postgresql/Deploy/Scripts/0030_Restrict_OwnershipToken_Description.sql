-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_OwnershipToken_Description_NotBlank'
          AND conrelid = '"dmscs"."OwnershipToken"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OwnershipToken"
            ADD CONSTRAINT "CK_OwnershipToken_Description_NotBlank"
            CHECK (length(regexp_replace("Description", '\s', '', 'g')) > 0);
    END IF;
END$$;
