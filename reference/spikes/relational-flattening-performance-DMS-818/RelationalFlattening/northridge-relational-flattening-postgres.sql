-- ============================================================================
-- Ed-Fi Data Management Service: Relational Flattening DDL Script (PostgreSQL)
-- ============================================================================
-- This script modifies the existing Ed-Fi tables in the Northridge database
-- to support the relational flattening design approach.
-- 
-- Key Changes:
-- 1. Add new surrogate key columns (SurrogateId BIGSERIAL PRIMARY KEY) to all tables
-- 2. Add Document reference columns (Document_Id, Document_PartitionKey)
-- 3. Add foreign key relationships using surrogate keys
-- 4. Remove redundant natural key columns from association tables
-- 5. Create unique constraints using surrogate keys for associations
-- 6. Add cascading delete constraints
-- 
-- IMPORTANT: Existing PRIMARY KEY constraints are dropped first using CASCADE
-- to allow replacement with surrogate key PRIMARY KEYs.
--
-- IMPORTANT: This script must be executed in the exact order presented
-- to avoid foreign key constraint violations.
-- ============================================================================

-- ============================================================================
-- Phase 1: Drop Existing Primary Keys to Allow Surrogate Key Replacement  
-- ============================================================================

-- Drop all existing primary key constraints that will conflict with surrogate keys
-- Using CASCADE to drop dependent foreign key constraints that will be recreated
ALTER TABLE edfi.student DROP CONSTRAINT IF EXISTS student_pk CASCADE;
ALTER TABLE edfi.school DROP CONSTRAINT IF EXISTS school_pk CASCADE;
ALTER TABLE edfi.descriptor DROP CONSTRAINT IF EXISTS descriptor_pk CASCADE;
ALTER TABLE edfi.exitwithdrawtypedescriptor DROP CONSTRAINT IF EXISTS exitwithdrawtypedescriptor_pk CASCADE;
ALTER TABLE edfi.studentschoolassociation DROP CONSTRAINT IF EXISTS studentschoolassociation_pk CASCADE;
ALTER TABLE edfi.studenteducationorganizationassociation DROP CONSTRAINT IF EXISTS studenteducationorganizationassociation_pk CASCADE;
ALTER TABLE edfi.studenteducationorganizationassociationrace DROP CONSTRAINT IF EXISTS studenteducationorganizationassociationrace_pk CASCADE;
ALTER TABLE edfi.studenteducationorganizationassociationstudentcharacteristic DROP CONSTRAINT IF EXISTS studenteducationorganizationassociationstudentcharacteristic_pk CASCADE;
ALTER TABLE edfi.studenteducationorganizationassociationstudentindicator DROP CONSTRAINT IF EXISTS studenteducationorganizationassociationstudentindicator_pk CASCADE;
ALTER TABLE edfi.studentprogramassociation DROP CONSTRAINT IF EXISTS studentprogramassociation_pk CASCADE;
ALTER TABLE edfi.generalstudentprogramassociation DROP CONSTRAINT IF EXISTS generalstudentprogramassociation_pk CASCADE;
ALTER TABLE edfi.studentspecialeducationprogramassociation DROP CONSTRAINT IF EXISTS studentspecialeducationprogramassociation_pk CASCADE;

-- ============================================================================
-- Phase 2: Add Surrogate Keys and Document References
-- ============================================================================

-- 1. edfi.student - Main student entity
ALTER TABLE edfi.student 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0;

-- Add unique constraint on original natural keys
ALTER TABLE edfi.student 
    ADD CONSTRAINT UQ_Student_Identity UNIQUE (changeversion, studentusi);

-- 2. edfi.school - School entity
ALTER TABLE edfi.school 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0;

-- Add unique constraint on original natural key
ALTER TABLE edfi.school 
    ADD CONSTRAINT UQ_School_Identity UNIQUE (schoolid);

-- 3. edfi.descriptor - Base descriptor table
ALTER TABLE edfi.descriptor 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0;

-- Add unique constraint on original natural keys
ALTER TABLE edfi.descriptor 
    ADD CONSTRAINT UQ_Descriptor_Identity UNIQUE (changeversion, descriptorid);

-- 4. edfi.exitwithdrawtypedescriptor - Exit/withdrawal type descriptor
ALTER TABLE edfi.exitwithdrawtypedescriptor 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0;

-- Add unique constraint on original natural key
ALTER TABLE edfi.exitwithdrawtypedescriptor 
    ADD CONSTRAINT UQ_ExitWithdrawTypeDescriptor_Identity 
    UNIQUE (exitwithdrawtypedescriptorid);

-- 5. edfi.studentschoolassociation - Student-school enrollment relationship
ALTER TABLE edfi.studentschoolassociation 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN Student_SurrogateId BIGINT,
    ADD COLUMN School_SurrogateId BIGINT;

-- 6. edfi.studenteducationorganizationassociation - Student-education org relationship
ALTER TABLE edfi.studenteducationorganizationassociation 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN Student_SurrogateId BIGINT,
    ADD COLUMN EducationOrganization_SurrogateId BIGINT;

-- 7. edfi.studenteducationorganizationassociationrace - Student race associations
ALTER TABLE edfi.studenteducationorganizationassociationrace 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN StudentEducationOrganizationAssociation_SurrogateId BIGINT,
    ADD COLUMN RaceDescriptor_SurrogateId BIGINT;

-- 8. edfi.studenteducationorganizationassociationstudentcharacteristic - Student characteristics
ALTER TABLE edfi.studenteducationorganizationassociationstudentcharacteristic 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN StudentEducationOrganizationAssociation_SurrogateId BIGINT,
    ADD COLUMN StudentCharacteristicDescriptor_SurrogateId BIGINT;

-- 9. edfi.studenteducationorganizationassociationstudentindicator - Student indicators
ALTER TABLE edfi.studenteducationorganizationassociationstudentindicator 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN StudentEducationOrganizationAssociation_SurrogateId BIGINT;

-- 10. edfi.studentprogramassociation - Student program associations
ALTER TABLE edfi.studentprogramassociation 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN Student_SurrogateId BIGINT;

-- 11. edfi.generalstudentprogramassociation - General student program association
ALTER TABLE edfi.generalstudentprogramassociation 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN Student_SurrogateId BIGINT;

-- 12. edfi.studentspecialeducationprogramassociation - Special education program association
ALTER TABLE edfi.studentspecialeducationprogramassociation 
    ADD COLUMN SurrogateId BIGSERIAL PRIMARY KEY,
    ADD COLUMN Document_Id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN Document_PartitionKey SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN Student_SurrogateId BIGINT;

-- ============================================================================
-- Phase 2: Populate Surrogate Key Relationships (BEFORE dropping columns)
-- ============================================================================

-- Populate Student_SurrogateId references
UPDATE edfi.studentschoolassociation 
SET Student_SurrogateId = s.SurrogateId
FROM edfi.student s
WHERE studentschoolassociation.studentusi = s.studentusi 
  AND studentschoolassociation.changeversion = s.changeversion;

-- Populate School_SurrogateId references  
UPDATE edfi.studentschoolassociation
SET School_SurrogateId = sch.SurrogateId
FROM edfi.school sch
WHERE studentschoolassociation.schoolid = sch.schoolid;

-- Populate StudentEducationOrganizationAssociation relationships
UPDATE edfi.studenteducationorganizationassociation
SET Student_SurrogateId = s.SurrogateId
FROM edfi.student s
WHERE studenteducationorganizationassociation.studentusi = s.studentusi 
  AND studenteducationorganizationassociation.changeversion = s.changeversion;

-- For education organization, we need to link to school table for now
UPDATE edfi.studenteducationorganizationassociation
SET EducationOrganization_SurrogateId = sch.SurrogateId
FROM edfi.school sch
WHERE studenteducationorganizationassociation.educationorganizationid = sch.schoolid;

-- Populate race associations
UPDATE edfi.studenteducationorganizationassociationrace
SET StudentEducationOrganizationAssociation_SurrogateId = seoa.SurrogateId
FROM edfi.studenteducationorganizationassociation seoa
WHERE studenteducationorganizationassociationrace.educationorganizationid = seoa.educationorganizationid
  AND studenteducationorganizationassociationrace.studentusi = seoa.studentusi;

UPDATE edfi.studenteducationorganizationassociationrace
SET RaceDescriptor_SurrogateId = d.SurrogateId  
FROM edfi.descriptor d
WHERE studenteducationorganizationassociationrace.racedescriptorid = d.descriptorid;

-- Populate student characteristic associations
UPDATE edfi.studenteducationorganizationassociationstudentcharacteristic
SET StudentEducationOrganizationAssociation_SurrogateId = seoa.SurrogateId
FROM edfi.studenteducationorganizationassociation seoa
WHERE studenteducationorganizationassociationstudentcharacteristic.educationorganizationid = seoa.educationorganizationid
  AND studenteducationorganizationassociationstudentcharacteristic.studentusi = seoa.studentusi;

UPDATE edfi.studenteducationorganizationassociationstudentcharacteristic
SET StudentCharacteristicDescriptor_SurrogateId = d.SurrogateId
FROM edfi.descriptor d
WHERE studenteducationorganizationassociationstudentcharacteristic.studentcharacteristicdescriptorid = d.descriptorid;

-- Populate student indicator associations
UPDATE edfi.studenteducationorganizationassociationstudentindicator
SET StudentEducationOrganizationAssociation_SurrogateId = seoa.SurrogateId
FROM edfi.studenteducationorganizationassociation seoa
WHERE studenteducationorganizationassociationstudentindicator.educationorganizationid = seoa.educationorganizationid
  AND studenteducationorganizationassociationstudentindicator.studentusi = seoa.studentusi;

-- Populate program associations
UPDATE edfi.studentprogramassociation
SET Student_SurrogateId = s.SurrogateId
FROM edfi.student s
WHERE studentprogramassociation.studentusi = s.studentusi;

UPDATE edfi.generalstudentprogramassociation
SET Student_SurrogateId = s.SurrogateId  
FROM edfi.student s
WHERE generalstudentprogramassociation.studentusi = s.studentusi
  AND generalstudentprogramassociation.changeversion = s.changeversion;

UPDATE edfi.studentspecialeducationprogramassociation
SET Student_SurrogateId = s.SurrogateId
FROM edfi.student s
WHERE studentspecialeducationprogramassociation.studentusi = s.studentusi;

-- ============================================================================
-- Phase 3: Add Foreign Key Constraints
-- ============================================================================

-- StudentSchoolAssociation foreign keys
ALTER TABLE edfi.studentschoolassociation
    ADD CONSTRAINT FK_SSA_Student
    FOREIGN KEY (Student_SurrogateId) REFERENCES edfi.student (SurrogateId) ON DELETE CASCADE,
    ADD CONSTRAINT FK_SSA_School
    FOREIGN KEY (School_SurrogateId) REFERENCES edfi.school (SurrogateId) ON DELETE CASCADE;

-- StudentEducationOrganizationAssociation foreign keys  
ALTER TABLE edfi.studenteducationorganizationassociation
    ADD CONSTRAINT FK_SEOA_Student
    FOREIGN KEY (Student_SurrogateId) REFERENCES edfi.student (SurrogateId) ON DELETE CASCADE,
    ADD CONSTRAINT FK_SEOA_School
    FOREIGN KEY (EducationOrganization_SurrogateId) REFERENCES edfi.school (SurrogateId) ON DELETE CASCADE;

-- Race association foreign keys
ALTER TABLE edfi.studenteducationorganizationassociationrace
    ADD CONSTRAINT FK_SEOARace_SEOA
    FOREIGN KEY (StudentEducationOrganizationAssociation_SurrogateId)
    REFERENCES edfi.studenteducationorganizationassociation (SurrogateId) ON DELETE CASCADE,
    ADD CONSTRAINT FK_SEOARace_RaceDesc
    FOREIGN KEY (RaceDescriptor_SurrogateId) REFERENCES edfi.descriptor (SurrogateId);

-- Student characteristic association foreign keys
ALTER TABLE edfi.studenteducationorganizationassociationstudentcharacteristic
    ADD CONSTRAINT FK_SEOASC_SEOA
    FOREIGN KEY (StudentEducationOrganizationAssociation_SurrogateId)
    REFERENCES edfi.studenteducationorganizationassociation (SurrogateId) ON DELETE CASCADE,
    ADD CONSTRAINT FK_SEOASC_Desc
    FOREIGN KEY (StudentCharacteristicDescriptor_SurrogateId) REFERENCES edfi.descriptor (SurrogateId);

-- Student indicator association foreign keys
ALTER TABLE edfi.studenteducationorganizationassociationstudentindicator
    ADD CONSTRAINT FK_SEOASI_SEOA
    FOREIGN KEY (StudentEducationOrganizationAssociation_SurrogateId)
    REFERENCES edfi.studenteducationorganizationassociation (SurrogateId) ON DELETE CASCADE;

-- Program association foreign keys
ALTER TABLE edfi.studentprogramassociation
    ADD CONSTRAINT FK_SPA_Student
    FOREIGN KEY (Student_SurrogateId) REFERENCES edfi.student (SurrogateId) ON DELETE CASCADE;

ALTER TABLE edfi.generalstudentprogramassociation
    ADD CONSTRAINT FK_GSPA_Student
    FOREIGN KEY (Student_SurrogateId) REFERENCES edfi.student (SurrogateId) ON DELETE CASCADE;

ALTER TABLE edfi.studentspecialeducationprogramassociation
    ADD CONSTRAINT FK_SSEPA_Student
    FOREIGN KEY (Student_SurrogateId) REFERENCES edfi.student (SurrogateId) ON DELETE CASCADE;

-- ============================================================================
-- Phase 4A: Drop Views and Triggers that Depend on Natural Key Columns
-- ============================================================================

-- Drop auth schema views that depend on natural key columns
DROP VIEW IF EXISTS auth.educationorganizationidtocontactusi CASCADE;
DROP VIEW IF EXISTS auth.educationorganizationidtocontactusiincludingdeletes CASCADE;
DROP VIEW IF EXISTS auth.educationorganizationidtostudentusi CASCADE;
DROP VIEW IF EXISTS auth.educationorganizationidtostudentusiincludingdeletes CASCADE;

-- Drop triggers that depend on natural key columns
DROP TRIGGER IF EXISTS handlekeychanges ON edfi.studentschoolassociation;

-- ============================================================================
-- Phase 4B: Remove Redundant Natural Key Columns from Association Tables
-- ============================================================================

-- Remove natural key columns from StudentSchoolAssociation (now using surrogate keys)
ALTER TABLE edfi.studentschoolassociation 
    DROP COLUMN IF EXISTS schoolid,
    DROP COLUMN IF EXISTS studentusi,
    DROP COLUMN IF EXISTS changeversion;

-- Remove natural key columns from StudentEducationOrganizationAssociation
ALTER TABLE edfi.studenteducationorganizationassociation
    DROP COLUMN IF EXISTS educationorganizationid,
    DROP COLUMN IF EXISTS studentusi,
    DROP COLUMN IF EXISTS changeversion;

-- Remove natural key columns from StudentEducationOrganizationAssociationRace
ALTER TABLE edfi.studenteducationorganizationassociationrace
    DROP COLUMN IF EXISTS educationorganizationid,
    DROP COLUMN IF EXISTS racedescriptorid,
    DROP COLUMN IF EXISTS studentusi;

-- Remove natural key columns from StudentEducationOrganizationAssociationStudentCharacteristic
ALTER TABLE edfi.studenteducationorganizationassociationstudentcharacteristic
    DROP COLUMN IF EXISTS educationorganizationid,
    DROP COLUMN IF EXISTS studentcharacteristicdescriptorid,
    DROP COLUMN IF EXISTS studentusi;

-- Remove natural key columns from StudentEducationOrganizationAssociationStudentIndicator
ALTER TABLE edfi.studenteducationorganizationassociationstudentindicator
    DROP COLUMN IF EXISTS educationorganizationid,
    DROP COLUMN IF EXISTS studentusi;

-- Remove natural key columns from StudentProgramAssociation
ALTER TABLE edfi.studentprogramassociation
    DROP COLUMN IF EXISTS studentusi;

-- Remove natural key columns from GeneralStudentProgramAssociation
ALTER TABLE edfi.generalstudentprogramassociation
    DROP COLUMN IF EXISTS studentusi,
    DROP COLUMN IF EXISTS changeversion;

-- Remove natural key columns from StudentSpecialEducationProgramAssociation
ALTER TABLE edfi.studentspecialeducationprogramassociation
    DROP COLUMN IF EXISTS studentusi;

-- Add unique constraints on surrogate key combinations to maintain data integrity
ALTER TABLE edfi.studentschoolassociation
    ADD CONSTRAINT UQ_SSA_SurrogateKeys
    UNIQUE (Student_SurrogateId, School_SurrogateId, entrydate);

ALTER TABLE edfi.studenteducationorganizationassociation
    ADD CONSTRAINT UQ_SEOA_SurrogateKeys
    UNIQUE (Student_SurrogateId, EducationOrganization_SurrogateId);

ALTER TABLE edfi.studenteducationorganizationassociationrace
    ADD CONSTRAINT UQ_SEOARace_SurrogateKeys
    UNIQUE (StudentEducationOrganizationAssociation_SurrogateId, RaceDescriptor_SurrogateId);

ALTER TABLE edfi.studenteducationorganizationassociationstudentcharacteristic
    ADD CONSTRAINT UQ_SEOASC_SurrogateKeys
    UNIQUE (StudentEducationOrganizationAssociation_SurrogateId, StudentCharacteristicDescriptor_SurrogateId);

ALTER TABLE edfi.studenteducationorganizationassociationstudentindicator
    ADD CONSTRAINT UQ_SEOASI_SurrogateKeys
    UNIQUE (StudentEducationOrganizationAssociation_SurrogateId, indicatorname);

ALTER TABLE edfi.studentprogramassociation
    ADD CONSTRAINT UQ_SPA_SurrogateKeys
    UNIQUE (Student_SurrogateId, begindate, educationorganizationid, programeducationorganizationid, programname, programtypedescriptorid);

ALTER TABLE edfi.generalstudentprogramassociation
    ADD CONSTRAINT UQ_GSPA_SurrogateKeys
    UNIQUE (Student_SurrogateId, begindate, educationorganizationid, programeducationorganizationid, programname, programtypedescriptorid);

ALTER TABLE edfi.studentspecialeducationprogramassociation
    ADD CONSTRAINT UQ_SSEPA_SurrogateKeys
    UNIQUE (Student_SurrogateId, begindate, educationorganizationid, programeducationorganizationid, programname, programtypedescriptorid);

-- ============================================================================
-- Phase 5: Create Performance Indexes on Surrogate Keys
-- ============================================================================

-- Document_Id indexes for document retrieval
CREATE INDEX IX_Student_Document_Id ON edfi.student (Document_Id);
CREATE INDEX IX_School_Document_Id ON edfi.school (Document_Id);
CREATE INDEX IX_Descriptor_Document_Id ON edfi.descriptor (Document_Id);
CREATE INDEX IX_ExitWithdrawTypeDescriptor_Document_Id ON edfi.exitwithdrawtypedescriptor (Document_Id);

-- Foreign key indexes for join performance
CREATE INDEX IX_StudentSchoolAssociation_Student_SurrogateId ON edfi.studentschoolassociation (Student_SurrogateId);
CREATE INDEX IX_StudentSchoolAssociation_School_SurrogateId ON edfi.studentschoolassociation (School_SurrogateId);
CREATE INDEX IX_SEOA_Student_SurrogateId ON edfi.studenteducationorganizationassociation (Student_SurrogateId);

CREATE INDEX IX_SEOARace_SEOA_SurrogateId
    ON edfi.studenteducationorganizationassociationrace (StudentEducationOrganizationAssociation_SurrogateId);

CREATE INDEX IX_SEOASC_SEOA_SurrogateId
    ON edfi.studenteducationorganizationassociationstudentcharacteristic (StudentEducationOrganizationAssociation_SurrogateId);

CREATE INDEX IX_SEOASI_SEOA_SurrogateId
    ON edfi.studenteducationorganizationassociationstudentindicator (StudentEducationOrganizationAssociation_SurrogateId);

-- Program association indexes
CREATE INDEX IX_StudentProgramAssociation_Student_SurrogateId ON edfi.studentprogramassociation (Student_SurrogateId);
CREATE INDEX IX_GeneralStudentProgramAssociation_Student_SurrogateId ON edfi.generalstudentprogramassociation (Student_SurrogateId);
CREATE INDEX IX_StudentSpecialEducationProgramAssociation_Student_SurrogateId ON edfi.studentspecialeducationprogramassociation (Student_SurrogateId);

-- Additional foreign key indexes for descriptor relationships
CREATE INDEX IX_SEOA_EducationOrganization_SurrogateId
    ON edfi.studenteducationorganizationassociation (EducationOrganization_SurrogateId);
CREATE INDEX IX_SEOARace_RaceDescriptor_SurrogateId
    ON edfi.studenteducationorganizationassociationrace (RaceDescriptor_SurrogateId);
CREATE INDEX IX_SEOASC_StudentCharacteristicDescriptor_SurrogateId
    ON edfi.studenteducationorganizationassociationstudentcharacteristic (StudentCharacteristicDescriptor_SurrogateId);

-- Document_Id indexes for all association tables
CREATE INDEX IX_StudentSchoolAssociation_Document_Id ON edfi.studentschoolassociation (Document_Id);
CREATE INDEX IX_SEOA_Document_Id ON edfi.studenteducationorganizationassociation (Document_Id);
CREATE INDEX IX_SEOARace_Document_Id ON edfi.studenteducationorganizationassociationrace (Document_Id);
CREATE INDEX IX_SEOASC_Document_Id ON edfi.studenteducationorganizationassociationstudentcharacteristic (Document_Id);
CREATE INDEX IX_SEOASI_Document_Id ON edfi.studenteducationorganizationassociationstudentindicator (Document_Id);
CREATE INDEX IX_StudentProgramAssociation_Document_Id ON edfi.studentprogramassociation (Document_Id);
CREATE INDEX IX_GeneralStudentProgramAssociation_Document_Id ON edfi.generalstudentprogramassociation (Document_Id);
CREATE INDEX IX_StudentSpecialEducationProgramAssociation_Document_Id ON edfi.studentspecialeducationprogramassociation (Document_Id);


-- ============================================================================
-- Relational Flattening Implementation Complete
-- ============================================================================

SELECT 'Relational Flattening DDL completed successfully!' AS Result;