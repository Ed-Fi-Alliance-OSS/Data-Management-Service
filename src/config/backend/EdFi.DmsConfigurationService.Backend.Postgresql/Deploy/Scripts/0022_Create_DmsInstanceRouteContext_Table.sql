-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dmscs.DmsInstanceRouteContext (
    Id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1) PRIMARY KEY,
    InstanceId BIGINT NOT NULL,
    ContextKey VARCHAR(256) NOT NULL,
    ContextValue VARCHAR(256) NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT fk_dmsinstanceroutecontext_instance FOREIGN KEY (InstanceId) REFERENCES dmscs.dmsInstance(Id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_dms_instance_routecontext_unique ON dmscs.DmsInstanceRouteContext (InstanceId, ContextKey);

COMMENT ON TABLE dmscs.DmsInstanceRouteContext IS 'Route context information for instances to support context-based routing (e.g., year-specific, district-specific deployments)';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.Id IS 'Instance route context id';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.InstanceId IS 'Instance id this route context belongs to';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.ContextKey IS 'Context key for routing (e.g., schoolYear, districtId)';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.ContextValue IS 'Context value for routing (e.g., 2024, 255901)';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.CreatedAt IS 'Timestamp when the record was created (UTC)';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.CreatedBy IS 'User or client ID who created the record';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.LastModifiedAt IS 'Timestamp when the record was last modified (UTC)';
COMMENT ON COLUMN dmscs.DmsInstanceRouteContext.ModifiedBy IS 'User or client ID who last modified the record';
