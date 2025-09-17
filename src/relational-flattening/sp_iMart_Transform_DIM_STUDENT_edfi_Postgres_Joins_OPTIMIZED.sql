-- OPTIMIZED PostgreSQL version of sp_iMart_Transform_DIM_STUDENT_edfi (Joins Version)
-- Performance improvements:
-- 1. Replaced multiple EXISTS subqueries with JOINs where possible
-- 2. Used materialized CTEs for frequently accessed data
-- 3. Optimized ROW_NUMBER() partitioning
-- 4. Reduced redundant joins in final assembly
-- 5. Added strategic temp table indexes for joins

CREATE OR REPLACE FUNCTION sp_iMart_Transform_DIM_STUDENT_edfi_Postgres_Joins_OPTIMIZED(
    p_SAID varchar(30) DEFAULT NULL,
    p_Batch_Period_List varchar(1000) DEFAULT NULL
)
RETURNS TABLE(
    -- Core Identifiers
    district_id varchar(15),
    eschool_building varchar(15),
    school_id varchar(30),
    school_year smallint,
    local_student_id varchar(25),
    current_enrolled_ind char(1),
    entry_date_value date,
    unique_id varchar(25),
    lea_student_id varchar(25),
    state_id_nbr varchar(25),
    ssn varchar(15),
    exclude_from_reporting_ind char(1),

    -- Name Information
    first_name varchar(35),
    middle_name varchar(15),
    last_name varchar(35),
    name_prefix varchar(10),
    name_suffix varchar(10),
    full_name varchar(80),
    preferred_name varchar(50),
    sort_name varchar(80),

    -- Academic Information
    homeroom_nbr varchar(25),
    year_entered_9th_grade smallint,
    class_of varchar(20),
    diploma_type_cd varchar(30),
    diploma_type_desc varchar(254),
    grade_level_cd varchar(30),
    grade_level_desc varchar(254),
    grade_level_cd_sort_order smallint,

    -- Demographics
    gender_cd varchar(30),
    gender_desc varchar(254),
    gender_cd_sort_order smallint,
    race_cd varchar(30),
    race_desc varchar(254),
    race_cd_sort_order smallint,
    birth_date date,
    place_of_birth varchar(80),
    state_of_birth varchar(80),
    country_of_birth varchar(80),

    -- Student Characteristics
    migrant_cd varchar(30),
    migrant_desc varchar(254),
    primary_language_cd varchar(30),
    primary_language_desc varchar(254),
    english_proficiency_cd varchar(30),
    english_proficiency_desc varchar(254),
    free_reduced_meal_cd varchar(30),
    free_reduced_meal_desc varchar(254),
    primary_disability_cd varchar(30),
    primary_disability_desc varchar(254),

    -- Indicator Flags
    disability_ind char(1),
    economically_disadvantaged_ind char(1),
    esol_ind char(1),
    gifted_ind char(1),
    limited_english_proficiency_ind char(1),
    migrant_ind char(1),
    homeless_ind char(1),
    military_connected_ind char(1),

    -- Race/Ethnicity Individual Indicators
    white_ind char(1),
    black_african_american_ind char(1),
    asian_ind char(1),
    hawaiian_pacific_islander_ind char(1),
    american_indian_alaskan_native_ind char(1),
    two_or_more_races_ind char(1),
    hispanic_latino_ethnicity_ind char(1),

    -- Performance metrics
    temp_tables int,
    processing_time_ms bigint
)
LANGUAGE plpgsql
AS $function$
DECLARE
    start_time timestamp;
    temp_count int := 0;
BEGIN
    start_time := clock_timestamp();

    -- Parse batch periods if provided
    DROP TABLE IF EXISTS temp_batch_periods CASCADE;
    CREATE TEMP TABLE temp_batch_periods (
        batch_period varchar(50) PRIMARY KEY
    );

    IF p_Batch_Period_List IS NOT NULL AND LENGTH(TRIM(p_Batch_Period_List)) > 0 THEN
        INSERT INTO temp_batch_periods (batch_period)
        SELECT TRIM(unnest(string_to_array(p_Batch_Period_List, ',')));
    END IF;

    -- OPTIMIZATION: Create a single comprehensive student enrollment CTE with all needed data
    DROP TABLE IF EXISTS temp_student_enrollment CASCADE;
    CREATE TEMP TABLE temp_student_enrollment AS
    WITH enrollment_base AS (
        SELECT
            st.surrogateid as student_surrogateid,
            st.studentusi,
            st.studentuniqueid,
            st.firstname,
            st.lastsurname,
            st.middlename,
            st.personaltitleprefix,
            st.generationcodesuffix,
            st.birthdate,
            st.birthcity,
            sc.surrogateid as school_surrogateid,
            sc.schoolid,
            sc.localeducationagencyid,
            ssa.entrydate,
            ssa.exitwithdrawdate,
            ssa.exitwithdrawtypedescriptorid,
            ssa.calendarcode,
            ssa.lastmodifieddate,
            ssa.schoolyear,
            ssa.entrygradeleveldescriptorid,
            -- Pre-calculate flags
            CASE WHEN ssa.calendarcode = 'R' THEN 0 ELSE 1 END as calendar_flag,
            CASE WHEN sc.schoolid::text LIKE '%Service%' OR sc.schoolid < 100
                 THEN 1 ELSE 0 END as serviceschool,
            CASE WHEN ssa.exitwithdrawtypedescriptorid IS NULL THEN 0 ELSE 1 END as withdrawal_flag
        FROM edfi.studentschoolassociation ssa
        INNER JOIN edfi.student st ON ssa.student_surrogateid = st.surrogateid
        INNER JOIN edfi.school sc ON ssa.school_surrogateid = sc.surrogateid
        WHERE ssa.enrollmenttypedescriptorid IS NOT NULL -- Filter for enrolled students early
    )
    SELECT
        *,
        ROW_NUMBER() OVER (
            PARTITION BY studentusi
            ORDER BY
                calendar_flag,
                serviceschool,
                withdrawal_flag,
                entrydate DESC,
                lastmodifieddate DESC
        ) as enrollment_priority
    FROM enrollment_base;

    CREATE INDEX idx_temp_enrollment_studentusi ON temp_student_enrollment(studentusi, enrollment_priority);
    CREATE INDEX idx_temp_enrollment_student_surrogateid ON temp_student_enrollment(student_surrogateid);
    temp_count := temp_count + 1;

    -- OPTIMIZATION: Consolidate race data with single pass
    DROP TABLE IF EXISTS temp_student_race CASCADE;
    CREATE TEMP TABLE temp_student_race AS
    SELECT DISTINCT
        st.studentusi,
        COALESCE(
            MAX(CASE WHEN d.codevalue = 'White' THEN 'White' END),
            MAX(CASE WHEN d.codevalue = 'Black - African American' THEN 'Black' END),
            MAX(CASE WHEN d.codevalue = 'Asian' THEN 'Asian' END),
            'Other'
        ) as race_code
    FROM edfi.studenteducationorganizationassociation seoa
    INNER JOIN edfi.student st ON seoa.student_surrogateid = st.surrogateid
    LEFT JOIN edfi.studenteducationorganizationassociationrace seoar
        ON seoar.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    LEFT JOIN edfi.descriptor d ON seoar.racedescriptor_surrogateid = d.surrogateid
    GROUP BY st.studentusi;

    CREATE INDEX idx_temp_race_studentusi ON temp_student_race(studentusi);
    temp_count := temp_count + 1;

    -- OPTIMIZATION: Gender and characteristics in single pass
    DROP TABLE IF EXISTS temp_student_demographics CASCADE;
    CREATE TEMP TABLE temp_student_demographics AS
    SELECT
        st.studentusi,
        MAX(gd.codevalue) as gender_code,
        MAX(CASE gd.codevalue
            WHEN 'Male' THEN 1
            WHEN 'Female' THEN 2
            ELSE 999 END) as gender_sort,
        MAX(CASE WHEN cd.codevalue LIKE '%Homeless%' THEN 'Y' ELSE 'N' END) as homeless_ind,
        MAX(CASE WHEN seoa.hispaniclatino = true THEN 'Y' ELSE 'N' END) as hispanic_ind
    FROM edfi.studenteducationorganizationassociation seoa
    INNER JOIN edfi.student st ON seoa.student_surrogateid = st.surrogateid
    LEFT JOIN edfi.descriptor gd ON seoa.sexdescriptorid = gd.descriptorid
    LEFT JOIN edfi.studenteducationorganizationassociationstudentcharacteristic seoac
        ON seoac.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    LEFT JOIN edfi.descriptor cd ON seoac.studentcharacteristicdescriptor_surrogateid = cd.surrogateid
    GROUP BY st.studentusi;

    CREATE INDEX idx_temp_demographics_studentusi ON temp_student_demographics(studentusi);
    temp_count := temp_count + 1;

    -- OPTIMIZATION: Grade level with descriptor join
    DROP TABLE IF EXISTS temp_student_grade CASCADE;
    CREATE TEMP TABLE temp_student_grade AS
    SELECT DISTINCT ON (tse.studentusi)
        tse.studentusi,
        d.codevalue as grade_code,
        CASE d.codevalue
            WHEN 'Kindergarten' THEN 0
            WHEN 'First grade' THEN 1
            WHEN 'Second grade' THEN 2
            WHEN 'Third grade' THEN 3
            WHEN 'Fourth grade' THEN 4
            WHEN 'Fifth grade' THEN 5
            WHEN 'Sixth grade' THEN 6
            WHEN 'Seventh grade' THEN 7
            WHEN 'Eighth grade' THEN 8
            WHEN 'Ninth grade' THEN 9
            WHEN 'Tenth grade' THEN 10
            WHEN 'Eleventh grade' THEN 11
            WHEN 'Twelfth grade' THEN 12
            ELSE 999
        END as grade_sort
    FROM temp_student_enrollment tse
    INNER JOIN edfi.descriptor d ON tse.entrygradeleveldescriptorid = d.descriptorid
    WHERE tse.enrollment_priority = 1
    ORDER BY tse.studentusi, tse.entrydate DESC;

    CREATE INDEX idx_temp_grade_studentusi ON temp_student_grade(studentusi);
    temp_count := temp_count + 1;

    -- OPTIMIZATION: Special education status
    DROP TABLE IF EXISTS temp_special_ed CASCADE;
    CREATE TEMP TABLE temp_special_ed AS
    SELECT DISTINCT
        st.studentusi,
        'Y' as disability_ind
    FROM edfi.studentspecialeducationprogramassociation ssepa
    INNER JOIN edfi.student st ON ssepa.student_surrogateid = st.surrogateid;

    CREATE INDEX idx_temp_special_ed_studentusi ON temp_special_ed(studentusi);
    temp_count := temp_count + 1;

    -- Final optimized query assembly
    RETURN QUERY
    SELECT
        -- Core Identifiers
        CAST(COALESCE(tse.localeducationagencyid::text, '--') AS varchar(15)) as district_id,
        CAST(COALESCE(tse.schoolid::text, '--') AS varchar(15)) as eschool_building,
        CAST(COALESCE(RIGHT(tse.schoolid::text, 4), '--') AS varchar(30)) as school_id,
        CAST(COALESCE(tse.schoolyear, 2018) AS smallint) as school_year,
        CAST(COALESCE(tse.studentuniqueid, '--') AS varchar(25)) as local_student_id,
        CAST(CASE WHEN tse.enrollment_priority = 1 AND tse.withdrawal_flag = 0
             THEN 'Y' ELSE 'N' END AS char(1)) as current_enrolled_ind,
        COALESCE(tse.entrydate, DATE '1753-01-01') as entry_date_value,
        CAST(COALESCE(tse.studentuniqueid, '--') AS varchar(25)) as unique_id,
        CAST(COALESCE(tse.studentuniqueid, '--') AS varchar(25)) as lea_student_id,
        CAST(COALESCE(tse.studentuniqueid, '--') AS varchar(25)) as state_id_nbr,
        CAST('--' AS varchar(15)) as ssn,
        CAST('-' AS char(1)) as exclude_from_reporting_ind,

        -- Name Information
        CAST(COALESCE(tse.firstname, '--') AS varchar(35)) as first_name,
        CAST(COALESCE(tse.middlename, '') AS varchar(15)) as middle_name,
        CAST(COALESCE(tse.lastsurname, '--') AS varchar(35)) as last_name,
        CAST(COALESCE(tse.personaltitleprefix, '') AS varchar(10)) as name_prefix,
        CAST(COALESCE(tse.generationcodesuffix, '') AS varchar(10)) as name_suffix,
        CAST(COALESCE(TRIM(COALESCE(tse.firstname, '') || ' ' ||
             COALESCE(tse.middlename, '') || ' ' ||
             COALESCE(tse.lastsurname, '') || ' ' ||
             COALESCE(tse.generationcodesuffix, '')), '--') AS varchar(80)) as full_name,
        CAST(COALESCE(tse.firstname, '--') AS varchar(50)) as preferred_name,
        CAST(COALESCE(TRIM(COALESCE(tse.lastsurname, '') || ' ' ||
             COALESCE(tse.generationcodesuffix, '') || ', ' ||
             COALESCE(tse.firstname, '') || ' ' ||
             COALESCE(tse.middlename, '')), '--') AS varchar(80)) as sort_name,

        -- Academic Information
        CAST('--' AS varchar(25)) as homeroom_nbr,
        CAST(0 AS smallint) as year_entered_9th_grade,
        CAST(COALESCE(tse.schoolid::text, '--') AS varchar(20)) as class_of,
        CAST('--' AS varchar(30)) as diploma_type_cd,
        CAST('--' AS varchar(254)) as diploma_type_desc,
        CAST(COALESCE(tsg.grade_code, '--') AS varchar(30)) as grade_level_cd,
        CAST(COALESCE(tsg.grade_code, '--') AS varchar(254)) as grade_level_desc,
        CAST(COALESCE(tsg.grade_sort, 0) AS smallint) as grade_level_cd_sort_order,

        -- Demographics
        CAST(COALESCE(tsd.gender_code, '--') AS varchar(30)) as gender_cd,
        CAST(COALESCE(tsd.gender_code, '--') AS varchar(254)) as gender_desc,
        CAST(COALESCE(tsd.gender_sort, 0) AS smallint) as gender_cd_sort_order,
        CAST(COALESCE(tsr.race_code, '--') AS varchar(30)) as race_cd,
        CAST(COALESCE(tsr.race_code, '--') AS varchar(254)) as race_desc,
        CAST(1 AS smallint) as race_cd_sort_order,
        COALESCE(tse.birthdate, DATE '1753-01-01') as birth_date,
        CAST(COALESCE(tse.birthcity, '--') AS varchar(80)) as place_of_birth,
        CAST('--' AS varchar(80)) as state_of_birth,
        CAST('--' AS varchar(80)) as country_of_birth,

        -- Student Characteristics
        CAST(CASE WHEN tsd.homeless_ind = 'Y' THEN 'M' ELSE '--' END AS varchar(30)) as migrant_cd,
        CAST('--' AS varchar(254)) as migrant_desc,
        CAST('English' AS varchar(30)) as primary_language_cd,
        CAST('English' AS varchar(254)) as primary_language_desc,
        CAST('--' AS varchar(30)) as english_proficiency_cd,
        CAST('--' AS varchar(254)) as english_proficiency_desc,
        CAST('--' AS varchar(30)) as free_reduced_meal_cd,
        CAST('--' AS varchar(254)) as free_reduced_meal_desc,
        CAST('--' AS varchar(30)) as primary_disability_cd,
        CAST('--' AS varchar(254)) as primary_disability_desc,

        -- Indicator Flags
        CAST(COALESCE(tspe.disability_ind, 'N') AS char(1)) as disability_ind,
        CAST('-' AS char(1)) as economically_disadvantaged_ind,
        CAST('-' AS char(1)) as esol_ind,
        CAST('-' AS char(1)) as gifted_ind,
        CAST('-' AS char(1)) as limited_english_proficiency_ind,
        CAST(CASE WHEN tsd.homeless_ind = 'Y' THEN 'Y' ELSE 'N' END AS char(1)) as migrant_ind,
        CAST(COALESCE(tsd.homeless_ind, 'N') AS char(1)) as homeless_ind,
        CAST('N' AS char(1)) as military_connected_ind,

        -- Race/Ethnicity Individual Indicators
        CAST(CASE WHEN tsr.race_code = 'White' THEN 'Y' ELSE 'N' END AS char(1)) as white_ind,
        CAST(CASE WHEN tsr.race_code = 'Black' THEN 'Y' ELSE 'N' END AS char(1)) as black_african_american_ind,
        CAST(CASE WHEN tsr.race_code = 'Asian' THEN 'Y' ELSE 'N' END AS char(1)) as asian_ind,
        CAST('N' AS char(1)) as hawaiian_pacific_islander_ind,
        CAST('N' AS char(1)) as american_indian_alaskan_native_ind,
        CAST('N' AS char(1)) as two_or_more_races_ind,
        CAST(COALESCE(tsd.hispanic_ind, 'N') AS char(1)) as hispanic_latino_ethnicity_ind,

        -- Performance metrics
        temp_count,
        CAST(EXTRACT(epoch FROM (clock_timestamp() - start_time)) * 1000 AS bigint) as processing_time_ms
    FROM temp_student_enrollment tse
    LEFT JOIN temp_student_race tsr ON tsr.studentusi = tse.studentusi
    LEFT JOIN temp_student_demographics tsd ON tsd.studentusi = tse.studentusi
    LEFT JOIN temp_student_grade tsg ON tsg.studentusi = tse.studentusi
    LEFT JOIN temp_special_ed tspe ON tspe.studentusi = tse.studentusi
    WHERE tse.enrollment_priority = 1
        AND (p_Batch_Period_List = 'all'
            OR p_Batch_Period_List IS NULL
            OR tse.schoolyear::varchar IN (SELECT batch_period FROM temp_batch_periods))
    ORDER BY tse.studentusi;

END;
$function$;
