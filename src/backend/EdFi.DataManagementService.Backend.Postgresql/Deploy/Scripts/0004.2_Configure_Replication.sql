-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

DO
$do$
IF NOT EXISTS (SELECT 1 FROM pg_replication_slots where slot_name = 'debezium') THEN
    PERFORM pg_create_logical_replication_slot('debezium', 'pgoutput');
END IF;
$do$;
