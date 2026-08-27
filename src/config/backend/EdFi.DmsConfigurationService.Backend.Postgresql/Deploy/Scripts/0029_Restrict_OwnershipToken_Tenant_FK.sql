-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OwnershipToken_Tenant'
          AND conrelid = '"dmscs"."OwnershipToken"'::regclass
          AND confdeltype <> 'r'
    ) THEN
        ALTER TABLE "dmscs"."OwnershipToken" DROP CONSTRAINT "FK_OwnershipToken_Tenant";
        ALTER TABLE "dmscs"."OwnershipToken" ADD CONSTRAINT "FK_OwnershipToken_Tenant" FOREIGN KEY ("TenantId") REFERENCES "dmscs"."Tenant"("Id") ON DELETE RESTRICT;
    END IF;
END$$;
