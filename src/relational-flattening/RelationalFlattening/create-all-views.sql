-- ============================================================================
-- Compatibility Views for Relational Flattening Design
-- Retrieves natural key columns from source tables using surrogate key relationships
-- ============================================================================

DROP VIEW IF EXISTS edfi.vw_studentschoolassociation CASCADE;

CREATE VIEW edfi.vw_studentschoolassociation AS
SELECT
    -- Original columns from flattened table
    ssa.surrogateId,
    ssa.entrydate,
    ssa.primaryschool,
    ssa.entrygradeleveldescriptorid,
    ssa.entrygradelevelreasondescriptorid,
    ssa.entrytypedescriptorid,
    ssa.repeatgradeindicator,
    ssa.classofschoolyear,
    ssa.schoolchoicetransfer,
    ssa.exitwithdrawdate,
    ssa.exitwithdrawtypedescriptorid,
    ssa.residencystatusdescriptorid,
    ssa.graduationplantypedescriptorid,
    ssa.educationorganizationid,
    ssa.graduationschoolyear,
    ssa.employedwhileenrolled,
    ssa.calendarcode,
    ssa.schoolyear,
    ssa.fulltimeequivalency,
    ssa.lastmodifieddate,
    ssa.document_id,
    ssa.document_partitionkey,
    ssa.student_surrogateid,
    ssa.school_surrogateid,

    -- Retrieved natural key columns
    s.studentusi,
    sch.schoolid
FROM edfi.studentschoolassociation ssa
    INNER JOIN edfi.student s ON ssa.student_surrogateid = s.surrogateid
    INNER JOIN edfi.school sch ON ssa.school_surrogateid = sch.surrogateid;

DROP VIEW IF EXISTS edfi.vw_studenteducationorganizationassociation CASCADE;

CREATE VIEW edfi.vw_studenteducationorganizationassociation AS
SELECT
    -- Original columns from flattened table
    seoa.surrogateId,
    seoa.sexdescriptorid,
    seoa.profilethumbnail,
    seoa.hispaniclatinoethnicity,
    seoa.limitedenglishproficiencydescriptorid,
    seoa.loginid,
    seoa.discriminator,
    seoa.createdate,
    seoa.lastmodifieddate,
    seoa.id,
    seoa.barriertointernetaccessinresidencedescriptorid,
    seoa.internetaccessinresidence,
    seoa.internetaccesstypeinresidencedescriptorid,
    seoa.internetperformanceinresidencedescriptorid,
    seoa.primarylearningdeviceawayfromschooldescriptorid,
    seoa.primarylearningdeviceproviderdescriptorid,
    seoa.primarylearningdeviceaccessdescriptorid,
    seoa.document_id,
    seoa.document_partitionkey,
    seoa.student_surrogateid,
    seoa.educationorganization_surrogateid,

    -- Retrieved natural key columns
    s.studentusi,
    sch.schoolid as educationorganizationid
FROM edfi.studenteducationorganizationassociation seoa
    INNER JOIN edfi.student s ON seoa.student_surrogateid = s.surrogateid
    INNER JOIN edfi.school sch ON seoa.educationorganization_surrogateid = sch.surrogateid;

DROP VIEW IF EXISTS edfi.vw_studenteducationorganizationassociationstudentcharacteristic CASCADE;

CREATE VIEW edfi.vw_studenteducationorganizationassociationstudentcharacteristic AS
SELECT
    -- Original columns from flattened table
    seoasc.surrogateid,
    seoasc.designatedby,
    seoasc.createdate,
    seoasc.document_id,
    seoasc.document_partitionkey,
    seoasc.studenteducationorganizationassociation_surrogateid,
    seoasc.studentcharacteristicdescriptor_surrogateid,

    -- Retrieved natural key columns
    s.studentusi,
    sch.schoolid as educationorganizationid,
    d.descriptorid as studentcharacteristicdescriptorid
FROM edfi.studenteducationorganizationassociationstudentcharacteristic seoasc
    INNER JOIN edfi.studenteducationorganizationassociation seoa
        ON seoasc.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    INNER JOIN edfi.student s ON seoa.student_surrogateid = s.surrogateid
    INNER JOIN edfi.school sch ON seoa.educationorganization_surrogateid = sch.surrogateid
    INNER JOIN edfi.descriptor d ON seoasc.studentcharacteristicdescriptor_surrogateid = d.surrogateid;

DROP VIEW IF EXISTS edfi.vw_studentprogramassociation CASCADE;

CREATE VIEW edfi.vw_studentprogramassociation AS
SELECT
    -- Original columns from flattened table
    spa.surrogateid,
    spa.begindate,
    spa.educationorganizationid,
    spa.programeducationorganizationid,
    spa.programname,
    spa.programtypedescriptorid,
    spa.document_id,
    spa.document_partitionkey,
    spa.student_surrogateid,

    -- Retrieved natural key columns
    s.studentusi
FROM edfi.studentprogramassociation spa
    INNER JOIN edfi.student s ON spa.student_surrogateid = s.surrogateid;

DROP VIEW IF EXISTS edfi.vw_studentspecialeducationprogramassociation CASCADE;

CREATE VIEW edfi.vw_studentspecialeducationprogramassociation AS
SELECT
    -- Original columns from flattened table
    ssepa.surrogateid,
    ssepa.begindate,
    ssepa.educationorganizationid,
    ssepa.programeducationorganizationid,
    ssepa.programname,
    ssepa.programtypedescriptorid,
    ssepa.document_id,
    ssepa.document_partitionkey,
    ssepa.student_surrogateid,

    -- Retrieved natural key columns
    s.studentusi
FROM edfi.studentspecialeducationprogramassociation ssepa
    INNER JOIN edfi.student s ON ssepa.student_surrogateid = s.surrogateid;

DROP VIEW IF EXISTS edfi.vw_studenteducationorganizationassociationstudentindicator CASCADE;

CREATE VIEW edfi.vw_studenteducationorganizationassociationstudentindicator AS
SELECT
    -- Original columns from flattened table
    seoasi.surrogateid,
    seoasi.indicatorname,
    seoasi.indicator,
    seoasi.designatedby,
    seoasi.createdate,
    seoasi.document_id,
    seoasi.document_partitionkey,
    seoasi.studenteducationorganizationassociation_surrogateid,

    -- Retrieved natural key columns
    s.studentusi,
    sch.schoolid as educationorganizationid
FROM edfi.studenteducationorganizationassociationstudentindicator seoasi
    INNER JOIN edfi.studenteducationorganizationassociation seoa
        ON seoasi.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    INNER JOIN edfi.student s ON seoa.student_surrogateid = s.surrogateid
    INNER JOIN edfi.school sch ON seoa.educationorganization_surrogateid = sch.surrogateid;
