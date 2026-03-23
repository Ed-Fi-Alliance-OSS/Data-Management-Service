-- DMS-843 Change Queries feature DDL sketch
-- This is an implementation-oriented PostgreSQL-flavored sketch for design review,
-- not a final migration script.

-- ---------------------------------------------------------------------------
-- Required core artifacts
-- ---------------------------------------------------------------------------

CREATE SEQUENCE IF NOT EXISTS dms.ChangeVersionSequence
    AS bigint
    START WITH 1
    INCREMENT BY 1;

CREATE TABLE IF NOT EXISTS dms.ResourceKey (
    ResourceKeyId smallint NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    CONSTRAINT PK_ResourceKey PRIMARY KEY (ResourceKeyId),
    CONSTRAINT UX_ResourceKey_ProjectName_ResourceName
        UNIQUE (ProjectName, ResourceName)
);

ALTER TABLE dms.Document
    ADD COLUMN IF NOT EXISTS ResourceKeyId smallint;

ALTER TABLE dms.Document
    ADD COLUMN IF NOT EXISTS ChangeVersion bigint;

ALTER TABLE dms.Document
    ADD COLUMN IF NOT EXISTS IdentityVersion bigint;

ALTER TABLE dms.Document
    ALTER COLUMN ChangeVersion SET DEFAULT nextval('dms.ChangeVersionSequence');

ALTER TABLE dms.Document
    ALTER COLUMN IdentityVersion SET DEFAULT nextval('dms.ChangeVersionSequence');

CREATE OR REPLACE FUNCTION dms.TF_Document_StampChangeVersion()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.ChangeVersion IS NULL THEN
            NEW.ChangeVersion := nextval('dms.ChangeVersionSequence');
        END IF;

        IF NEW.IdentityVersion IS NULL THEN
            NEW.IdentityVersion := nextval('dms.ChangeVersionSequence');
        END IF;

        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE' THEN
        IF NEW.EdfiDoc IS DISTINCT FROM OLD.EdfiDoc THEN
            NEW.ChangeVersion := nextval('dms.ChangeVersionSequence');
        END IF;

        RETURN NEW;
    END IF;

    RETURN NEW;
END;
$$;

-- Enable the stamp triggers only after live-row and journal backfill complete.

-- IdentityVersion updates for identity-changing writes are application-managed,
-- because generic database triggers do not have access to resource-specific
-- IdentityJsonPaths metadata in the current backend.

CREATE TABLE IF NOT EXISTS dms.DocumentDeleteTracking (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    DocumentUuid uuid NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    IsDescriptor boolean NOT NULL,
    KeyValues jsonb NOT NULL,
    SecurityElements jsonb NOT NULL,
    StudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StudentEdOrgResponsibilityAuthorizationIds jsonb NULL,
    ContactStudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StaffEducationOrganizationAuthorizationEdOrgIds jsonb NULL,
    DeletedAt timestamp NOT NULL DEFAULT now(),
    CONSTRAINT PK_DocumentDeleteTracking
        PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId)
);

CREATE TABLE IF NOT EXISTS dms.DocumentKeyChangeTracking (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    DocumentUuid uuid NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    IsDescriptor boolean NOT NULL,
    OldKeyValues jsonb NOT NULL,
    NewKeyValues jsonb NOT NULL,
    SecurityElements jsonb NOT NULL,
    StudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StudentEdOrgResponsibilityAuthorizationIds jsonb NULL,
    ContactStudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StaffEducationOrganizationAuthorizationEdOrgIds jsonb NULL,
    ChangedAt timestamp NOT NULL DEFAULT now(),
    CONSTRAINT PK_DocumentKeyChangeTracking
        PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId)
);

CREATE TABLE IF NOT EXISTS dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    ResourceKeyId smallint NOT NULL,
    CreatedAt timestamp NOT NULL DEFAULT now(),
    CONSTRAINT PK_DocumentChangeEvent
        PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId),
    CONSTRAINT FK_DocumentChangeEvent_Document
        FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id)
        ON DELETE CASCADE,
    CONSTRAINT FK_DocumentChangeEvent_ResourceKey
        FOREIGN KEY (ResourceKeyId)
        REFERENCES dms.ResourceKey (ResourceKeyId)
);

CREATE INDEX IF NOT EXISTS IX_Document_ResourceKeyId_DocumentId
    ON dms.Document (ResourceKeyId, DocumentPartitionKey, Id);

CREATE INDEX IF NOT EXISTS IX_DocumentDeleteTracking_Project_Resource_ChangeVersion
    ON dms.DocumentDeleteTracking
    (
        ProjectName,
        ResourceName,
        ChangeVersion,
        DocumentPartitionKey,
        DocumentId
    );

CREATE INDEX IF NOT EXISTS IX_DocumentKeyChangeTracking_Project_Resource_ChangeVersion
    ON dms.DocumentKeyChangeTracking
    (
        ProjectName,
        ResourceName,
        ChangeVersion,
        DocumentPartitionKey,
        DocumentId
    );

CREATE INDEX IF NOT EXISTS IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion
    ON dms.DocumentChangeEvent
    (
        ResourceKeyId,
        ChangeVersion,
        DocumentPartitionKey,
        DocumentId
    );

CREATE INDEX IF NOT EXISTS IX_DocumentChangeEvent_Document
    ON dms.DocumentChangeEvent (DocumentPartitionKey, DocumentId);

CREATE OR REPLACE FUNCTION dms.TF_Document_JournalChangeVersion()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO dms.DocumentChangeEvent
        (
            ChangeVersion,
            DocumentPartitionKey,
            DocumentId,
            ResourceKeyId
        )
        VALUES
        (
            NEW.ChangeVersion,
            NEW.DocumentPartitionKey,
            NEW.Id,
            NEW.ResourceKeyId
        );

        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE' AND NEW.ChangeVersion IS DISTINCT FROM OLD.ChangeVersion THEN
        INSERT INTO dms.DocumentChangeEvent
        (
            ChangeVersion,
            DocumentPartitionKey,
            DocumentId,
            ResourceKeyId
        )
        VALUES
        (
            NEW.ChangeVersion,
            NEW.DocumentPartitionKey,
            NEW.Id,
            NEW.ResourceKeyId
        );
    END IF;

    RETURN NEW;
END;
$$;

-- Enable the journal trigger only after journal backfill completes.

-- ---------------------------------------------------------------------------
-- Deterministic backfill sketches
-- ---------------------------------------------------------------------------

-- ResourceKey seeds must be emitted from the deployed effective schema manifest
-- rather than discovered from the current contents of dms.Document.
-- Provision one row for every resource in the effective schema, including
-- resources that currently have zero live rows.
--
-- Example shape:
-- INSERT INTO dms.ResourceKey
-- (
--     ResourceKeyId,
--     ProjectName,
--     ResourceName,
--     ResourceVersion
-- )
-- VALUES
--     (1, 'Ed-Fi', 'Student', '5.2.0'),
--     ...
-- ON CONFLICT (ResourceKeyId) DO NOTHING;

-- Use an ordered procedural loop so sequence values are consumed in the
-- declared backfill order. Do not replace this with UPDATE ... FROM plus
-- nextval(...), because PostgreSQL does not guarantee row-update order there.
DO $$
DECLARE
    backfill_row record;
    resolved_resource_key_id smallint;
BEGIN
    FOR backfill_row IN
        SELECT
            DocumentPartitionKey,
            Id,
            ProjectName,
            ResourceName
        FROM dms.Document
        WHERE ResourceKeyId IS NULL
           OR ChangeVersion IS NULL
           OR IdentityVersion IS NULL
        ORDER BY DocumentPartitionKey, Id
    LOOP
        SELECT rk.ResourceKeyId
        INTO resolved_resource_key_id
        FROM dms.ResourceKey rk
        WHERE rk.ProjectName = backfill_row.ProjectName
          AND rk.ResourceName = backfill_row.ResourceName;

        UPDATE dms.Document
        SET ResourceKeyId = COALESCE(ResourceKeyId, resolved_resource_key_id),
            ChangeVersion = COALESCE(ChangeVersion, nextval('dms.ChangeVersionSequence')),
            IdentityVersion = COALESCE(IdentityVersion, nextval('dms.ChangeVersionSequence'))
        WHERE DocumentPartitionKey = backfill_row.DocumentPartitionKey
          AND Id = backfill_row.Id;
    END LOOP;
END $$;

ALTER TABLE dms.Document
    ALTER COLUMN ResourceKeyId SET NOT NULL;

ALTER TABLE dms.Document
    ALTER COLUMN ChangeVersion SET NOT NULL;

ALTER TABLE dms.Document
    ALTER COLUMN IdentityVersion SET NOT NULL;

ALTER TABLE dms.Document
    ADD CONSTRAINT FK_Document_ResourceKey
        FOREIGN KEY (ResourceKeyId)
        REFERENCES dms.ResourceKey (ResourceKeyId);

INSERT INTO dms.DocumentChangeEvent
(
    ChangeVersion,
    DocumentPartitionKey,
    DocumentId,
    ResourceKeyId,
    CreatedAt
)
SELECT
    d.ChangeVersion,
    d.DocumentPartitionKey,
    d.Id,
    d.ResourceKeyId,
    now()
FROM dms.Document d
LEFT JOIN dms.DocumentChangeEvent e
    ON e.DocumentPartitionKey = d.DocumentPartitionKey
   AND e.DocumentId = d.Id
   AND e.ChangeVersion = d.ChangeVersion
WHERE e.DocumentId IS NULL;

-- Enable triggers only after live-row and journal backfill complete so rollout
-- does not double-journal historical rows and does not contradict the intended
-- deployment order.
DROP TRIGGER IF EXISTS TR_Document_StampChangeVersion_Insert ON dms.Document;
CREATE TRIGGER TR_Document_StampChangeVersion_Insert
BEFORE INSERT ON dms.Document
FOR EACH ROW
EXECUTE FUNCTION dms.TF_Document_StampChangeVersion();

DROP TRIGGER IF EXISTS TR_Document_StampChangeVersion_Update ON dms.Document;
CREATE TRIGGER TR_Document_StampChangeVersion_Update
BEFORE UPDATE OF EdfiDoc ON dms.Document
FOR EACH ROW
EXECUTE FUNCTION dms.TF_Document_StampChangeVersion();

DROP TRIGGER IF EXISTS TR_Document_JournalChangeVersion ON dms.Document;
CREATE TRIGGER TR_Document_JournalChangeVersion
AFTER INSERT OR UPDATE OF ChangeVersion ON dms.Document
FOR EACH ROW
EXECUTE FUNCTION dms.TF_Document_JournalChangeVersion();
