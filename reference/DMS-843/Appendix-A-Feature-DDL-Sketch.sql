-- DMS-843 Change Queries feature DDL sketch
-- This is an implementation-oriented sketch for design review, not a final migration script.

-- ---------------------------------------------------------------------------
-- Required core artifacts
-- ---------------------------------------------------------------------------

CREATE SEQUENCE IF NOT EXISTS dms.ChangeVersionSequence
    AS bigint
    START WITH 1
    INCREMENT BY 1;

ALTER TABLE dms.Document
    ADD COLUMN IF NOT EXISTS ChangeVersion bigint;

ALTER TABLE dms.Document
    ALTER COLUMN ChangeVersion SET DEFAULT nextval('dms.ChangeVersionSequence');

CREATE OR REPLACE FUNCTION dms.TF_Document_StampChangeVersion()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.ChangeVersion IS NULL THEN
            NEW.ChangeVersion := nextval('dms.ChangeVersionSequence');
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

CREATE INDEX IF NOT EXISTS IX_Document_Project_Resource_ChangeVersion
    ON dms.Document (ProjectName, ResourceName, ChangeVersion, DocumentPartitionKey, Id);

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

-- Optional supporting index if needed by validation.
CREATE INDEX IF NOT EXISTS IX_Document_ChangeVersion
    ON dms.Document (ChangeVersion);

-- Deterministic one-time live-row backfill sketch.
WITH ordered_documents AS (
    SELECT
        DocumentPartitionKey,
        Id
    FROM dms.Document
    WHERE ChangeVersion IS NULL
    ORDER BY DocumentPartitionKey, Id
)
UPDATE dms.Document d
SET ChangeVersion = nextval('dms.ChangeVersionSequence')
FROM ordered_documents o
WHERE d.DocumentPartitionKey = o.DocumentPartitionKey
  AND d.Id = o.Id;

ALTER TABLE dms.Document
    ALTER COLUMN ChangeVersion SET NOT NULL;

-- ---------------------------------------------------------------------------
-- Optional internal live-change journal
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    IsDescriptor boolean NOT NULL,
    CreatedAt timestamp NOT NULL DEFAULT now(),
    CONSTRAINT PK_DocumentChangeEvent
        PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId),
    CONSTRAINT FK_DocumentChangeEvent_Document
        FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_DocumentChangeEvent_Resource_ChangeVersion
    ON dms.DocumentChangeEvent
    (
        ProjectName,
        ResourceName,
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
            ProjectName,
            ResourceName,
            ResourceVersion,
            IsDescriptor
        )
        VALUES
        (
            NEW.ChangeVersion,
            NEW.DocumentPartitionKey,
            NEW.Id,
            NEW.ProjectName,
            NEW.ResourceName,
            NEW.ResourceVersion,
            NEW.IsDescriptor
        );

        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE' AND NEW.ChangeVersion IS DISTINCT FROM OLD.ChangeVersion THEN
        INSERT INTO dms.DocumentChangeEvent
        (
            ChangeVersion,
            DocumentPartitionKey,
            DocumentId,
            ProjectName,
            ResourceName,
            ResourceVersion,
            IsDescriptor
        )
        VALUES
        (
            NEW.ChangeVersion,
            NEW.DocumentPartitionKey,
            NEW.Id,
            NEW.ProjectName,
            NEW.ResourceName,
            NEW.ResourceVersion,
            NEW.IsDescriptor
        );
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS TR_Document_JournalChangeVersion ON dms.Document;
CREATE TRIGGER TR_Document_JournalChangeVersion
AFTER INSERT OR UPDATE OF ChangeVersion ON dms.Document
FOR EACH ROW
EXECUTE FUNCTION dms.TF_Document_JournalChangeVersion();

-- Optional journal backfill sketch.
INSERT INTO dms.DocumentChangeEvent
(
    ChangeVersion,
    DocumentPartitionKey,
    DocumentId,
    ProjectName,
    ResourceName,
    ResourceVersion,
    IsDescriptor,
    CreatedAt
)
SELECT
    d.ChangeVersion,
    d.DocumentPartitionKey,
    d.Id,
    d.ProjectName,
    d.ResourceName,
    d.ResourceVersion,
    d.IsDescriptor,
    now()
FROM dms.Document d
LEFT JOIN dms.DocumentChangeEvent e
    ON e.DocumentPartitionKey = d.DocumentPartitionKey
   AND e.DocumentId = d.Id
   AND e.ChangeVersion = d.ChangeVersion
WHERE e.DocumentId IS NULL;
