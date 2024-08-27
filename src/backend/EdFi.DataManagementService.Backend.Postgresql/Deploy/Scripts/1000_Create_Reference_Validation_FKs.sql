-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Reference validation enforcement occurs via this constraint
-- Omitting this FK effectively disables reference validation
ALTER TABLE dms.Reference
ADD CONSTRAINT FK_Reference_ReferencedAlias FOREIGN KEY (ReferentialPartitionKey, ReferentialId)
REFERENCES dms.Alias (ReferentialPartitionKey, ReferentialId) ON DELETE RESTRICT ON UPDATE CASCADE;
