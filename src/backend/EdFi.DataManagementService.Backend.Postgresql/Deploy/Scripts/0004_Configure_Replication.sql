-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

DROP PUBLICATION IF EXISTS to_debezium;
CREATE PUBLICATION to_debezium WITH (publish = 'insert, update, delete, truncate', publish_via_partition_root = true);

DO
$do
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_replication_slots where slot_name = 'debezium') THEN
        SELECT pg_create_logical_replication_slot('debezium', 'pgoutput');
    END IF;
END
$do$

ALTER PUBLICATION to_debezium ADD TABLE dms.document;
