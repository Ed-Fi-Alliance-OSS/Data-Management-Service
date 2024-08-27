-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Incorrect replication might have been setup by Debezium before this can run.
-- Delete and recreate correctly with partition support.
DROP PUBLICATION IF EXISTS to_debezium;
CREATE PUBLICATION to_debezium FOR TABLE dms.document WITH (publish = 'insert, update, delete, truncate', publish_via_partition_root = true);
