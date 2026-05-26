-- Simplified PostgreSQL student function with surrogate key joins (only temp_gender and temp_characteristics)
CREATE OR REPLACE FUNCTION sp_STUDENT_SIMPLE_Postgres_Joins(
    p_SAID varchar(30) DEFAULT NULL,
    p_Batch_Period_List varchar(1000) DEFAULT NULL
)
RETURNS TABLE(
    -- Core Identifiers
    district_id varchar(15),
    school_id varchar(30),
    school_year smallint,
    local_student_id varchar(25),
    unique_id varchar(25),

    -- Name Information
    first_name varchar(35),
    middle_name varchar(15),
    last_name varchar(35),
    full_name varchar(80),

    -- Demographics
    gender_cd varchar(30),
    gender_desc varchar(254),
    gender_cd_sort_order smallint,
    birth_date date,

    -- Student Characteristics
    homeless_ind char(1)
)
LANGUAGE plpgsql
AS $function$
DECLARE
BEGIN

    -- CLEANUP: Drop temp tables from previous runs
    DROP TABLE IF EXISTS temp_gender CASCADE;
    DROP TABLE IF EXISTS temp_characteristics CASCADE;

    -- TEMP TABLE 1: Gender (JOINS VERSION with surrogate keys)
    DROP TABLE IF EXISTS temp_gender;
    CREATE TEMP TABLE temp_gender AS
    SELECT st.studentusi, d.codevalue as gender_code,
           CASE d.codevalue WHEN 'Male' THEN 1 WHEN 'Female' THEN 2 ELSE 999 END as gender_sort
    FROM edfi.studenteducationorganizationassociation seoa
    -- JOINS VERSION: Get studentusi from student table using surrogate key
    JOIN edfi.student st ON seoa.student_surrogateid = st.surrogateid
    -- JOINS VERSION: Join descriptor using original descriptorid (sex descriptor still uses original ID)
    JOIN edfi.descriptor d ON seoa.sexdescriptorid = d.descriptorid;

    -- TEMP TABLE 2: Characteristics (JOINS VERSION with surrogate keys)
    DROP TABLE IF EXISTS temp_characteristics;
    CREATE TEMP TABLE temp_characteristics AS
    SELECT st.studentusi,
           CASE WHEN EXISTS(SELECT 1 FROM edfi.studenteducationorganizationassociationstudentcharacteristic sc2
                           -- JOINS VERSION: Join descriptor using surrogate key
                           JOIN edfi.descriptor d2 ON sc2.studentcharacteristicdescriptor_surrogateid = d2.surrogateid
                           -- JOINS VERSION: Join association using surrogate key
                           JOIN edfi.studenteducationorganizationassociation seoa2 ON sc2.studenteducationorganizationassociation_surrogateid = seoa2.surrogateid
                           -- JOINS VERSION: Get studentusi from student table using surrogate key
                           JOIN edfi.student st2 ON seoa2.student_surrogateid = st2.surrogateid
                           WHERE st2.studentusi = st.studentusi AND d2.codevalue LIKE '%Homeless%')
                THEN 'Y' ELSE 'N' END as homeless_ind
    FROM edfi.studenteducationorganizationassociation seoa
    -- JOINS VERSION: Get studentusi from student table using surrogate key
    JOIN edfi.student st ON seoa.student_surrogateid = st.surrogateid;

    RETURN QUERY
    SELECT
        -- Core Identifiers
        CAST(COALESCE(s.localeducationagencyid::text, s.schoolid::text) AS varchar(15)) as district_id,
        CAST(COALESCE(s.schoolid::text, '0') AS varchar(30)) as school_id,
        CAST(2018 AS smallint) as school_year,
        CAST(COALESCE(st.studentuniqueid, '0') AS varchar(25)) as local_student_id,
        CAST(COALESCE(st.studentuniqueid, '0') AS varchar(25)) as unique_id,

        -- Name Information
        CAST(COALESCE(st.firstname, '') AS varchar(35)) as first_name,
        CAST(COALESCE(st.middlename, '') AS varchar(15)) as middle_name,
        CAST(COALESCE(st.lastsurname, '') AS varchar(35)) as last_name,
        CAST(COALESCE(TRIM(COALESCE(st.firstname, '') || ' ' || COALESCE(st.middlename, '') || ' ' || COALESCE(st.lastsurname, '')), '') AS varchar(80)) as full_name,

        -- Demographics
        CAST(COALESCE(tg.gender_code, '') AS varchar(30)) as gender_cd,
        CAST(COALESCE(tg.gender_code, '') AS varchar(254)) as gender_desc,
        CAST(COALESCE(tg.gender_sort, 0) AS smallint) as gender_cd_sort_order,
        COALESCE(st.birthdate, DATE '1900-01-01') as birth_date,

        -- Student Characteristics
        CAST(COALESCE(tc.homeless_ind, 'N') AS char(1)) as homeless_ind

    FROM edfi.student st
    LEFT JOIN edfi.school s ON s.schoolid = (
        SELECT sc.schoolid
        FROM edfi.studentschoolassociation ssa
        -- JOINS VERSION: Get schoolid from school table using surrogate key
        JOIN edfi.school sc ON ssa.school_surrogateid = sc.surrogateid
        -- JOINS VERSION: Match student using surrogate key
        WHERE ssa.student_surrogateid = st.surrogateid
        ORDER BY ssa.entrydate DESC
        LIMIT 1
    )
    LEFT JOIN temp_gender tg ON tg.studentusi = st.studentusi
    LEFT JOIN temp_characteristics tc ON tc.studentusi = st.studentusi
    ORDER BY st.studentuniqueid;

END;
$function$;