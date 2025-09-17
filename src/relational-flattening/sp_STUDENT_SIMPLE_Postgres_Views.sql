-- Simplified PostgreSQL student function with views (only temp_gender and temp_characteristics)
CREATE OR REPLACE FUNCTION sp_STUDENT_SIMPLE_Postgres_Views(
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

    -- TEMP TABLE 1: Gender (VIEWS VERSION using compatibility views)
    DROP TABLE IF EXISTS temp_gender;
    CREATE TEMP TABLE temp_gender AS
    SELECT seoa.studentusi, d.codevalue as gender_code,
           CASE d.codevalue WHEN 'Male' THEN 1 WHEN 'Female' THEN 2 ELSE 999 END as gender_sort
    FROM edfi.vw_studenteducationorganizationassociation seoa
    JOIN edfi.descriptor d ON seoa.sexdescriptorid = d.descriptorid;

    -- TEMP TABLE 2: Characteristics (VIEWS VERSION using compatibility views)
    DROP TABLE IF EXISTS temp_characteristics;
    CREATE TEMP TABLE temp_characteristics AS
    SELECT seoa.studentusi,
           CASE WHEN EXISTS(SELECT 1 FROM edfi.vw_studenteducationorganizationassociationstudentcharacteristic sc2
                           JOIN edfi.descriptor d2 ON sc2.studentcharacteristicdescriptorid = d2.descriptorid
                           WHERE sc2.studentusi = seoa.studentusi AND d2.codevalue LIKE '%Homeless%')
                THEN 'Y' ELSE 'N' END as homeless_ind
    FROM edfi.vw_studenteducationorganizationassociation seoa;

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
        SELECT ssa.schoolid
        FROM edfi.vw_studentschoolassociation ssa
        WHERE ssa.studentusi = st.studentusi
        ORDER BY ssa.entrydate DESC
        LIMIT 1
    )
    LEFT JOIN temp_gender tg ON tg.studentusi = st.studentusi
    LEFT JOIN temp_characteristics tc ON tc.studentusi = st.studentusi
    ORDER BY st.studentuniqueid;

END;
$function$;