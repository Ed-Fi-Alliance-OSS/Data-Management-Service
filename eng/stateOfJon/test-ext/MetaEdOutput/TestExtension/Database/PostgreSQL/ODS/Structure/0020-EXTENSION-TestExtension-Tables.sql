-- Table testextension.HairColorDescriptor --
CREATE TABLE testextension.HairColorDescriptor (
    HairColorDescriptorId INT NOT NULL,
    CONSTRAINT HairColorDescriptor_PK PRIMARY KEY (HairColorDescriptorId)
);

-- Table testextension.StudentEducationOrganizationAssociationExtension --
CREATE TABLE testextension.StudentEducationOrganizationAssociationExtension (
    EducationOrganizationId BIGINT NOT NULL,
    StudentUSI INT NOT NULL,
    HairColorDescriptorId INT NULL,
    CreateDate TIMESTAMP NOT NULL,
    CONSTRAINT StudentEducationOrganizationAssociationExtension_PK PRIMARY KEY (EducationOrganizationId, StudentUSI)
);
ALTER TABLE testextension.StudentEducationOrganizationAssociationExtension ALTER COLUMN CreateDate SET DEFAULT current_timestamp AT TIME ZONE 'UTC';

