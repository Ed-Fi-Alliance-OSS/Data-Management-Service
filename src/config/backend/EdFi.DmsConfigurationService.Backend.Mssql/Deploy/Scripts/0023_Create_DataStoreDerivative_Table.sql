-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

IF OBJECT_ID('dmscs.DataStoreDerivative', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.DataStoreDerivative (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        DataStoreId BIGINT NOT NULL,
        DerivativeType NVARCHAR(50) NOT NULL,
        ConnectionString VARBINARY(MAX),
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        CONSTRAINT fk_datastorederivative_datastore FOREIGN KEY (DataStoreId) REFERENCES dmscs.DataStore(Id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_datastorederivative_datastoreid' AND object_id = OBJECT_ID('dmscs.DataStoreDerivative'))
    CREATE INDEX idx_datastorederivative_datastoreid ON dmscs.DataStoreDerivative (DataStoreId);
