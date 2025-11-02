-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Unlogged staging table used by InsertReferences to avoid per-session TEMP table churn and WAL writes.
CREATE UNLOGGED TABLE IF NOT EXISTS dms.ReferenceStage
(
    SessionId                      INTEGER  NOT NULL,
    ParentDocumentId               BIGINT   NOT NULL,
    ParentDocumentPartitionKey     SMALLINT NOT NULL,
    ReferentialPartitionKey        SMALLINT NOT NULL,
    ReferentialId                  UUID     NOT NULL,
    AliasId                        BIGINT,
    ReferencedDocumentId           BIGINT,
    ReferencedDocumentPartitionKey SMALLINT,
    CONSTRAINT PK_ReferenceStage PRIMARY KEY (SessionId, ReferentialPartitionKey, ReferentialId)
)
WITH (autovacuum_enabled = TRUE);

CREATE INDEX IF NOT EXISTS IX_ReferenceStage_Alias
    ON dms.ReferenceStage (SessionId, AliasId);

CREATE INDEX IF NOT EXISTS IX_ReferenceStage_Parent
    ON dms.ReferenceStage (SessionId, ParentDocumentPartitionKey, ParentDocumentId);
