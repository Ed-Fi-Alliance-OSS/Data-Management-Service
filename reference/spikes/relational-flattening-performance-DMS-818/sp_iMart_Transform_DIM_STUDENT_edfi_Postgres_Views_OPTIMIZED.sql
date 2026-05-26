-- PostgreSQL OPTIMIZED version of sp_iMart_Transform_DIM_STUDENT_edfi using Views
-- Performance optimizations:
-- 1. Reduced temp table creation overhead by combining related operations
-- 2. Added indexes on temp tables for join columns
-- 3. Used more efficient data aggregation strategies
-- 4. Eliminated redundant EXISTS checks
-- 5. Optimized join order based on selectivity

CREATE OR REPLACE FUNCTION sp_iMart_Transform_DIM_STUDENT_edfi_Postgres_Views_Optimized(
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

    -- Batch period processing
    DROP TABLE IF EXISTS temp_batch_periods;
    CREATE TEMP TABLE temp_batch_periods (
        batch_period varchar(50) PRIMARY KEY
    );

    IF p_Batch_Period_List IS NOT NULL AND LENGTH(TRIM(p_Batch_Period_List)) > 0 THEN
        INSERT INTO temp_batch_periods (batch_period)
        SELECT TRIM(unnest(string_to_array(p_Batch_Period_List, ',')));
    END IF;

    -- OPTIMIZATION 1: Combined enrollment and calendar data with all needed fields in one pass
    DROP TABLE IF EXISTS temp_enrollment_calendar;
    CREATE TEMP TABLE temp_enrollment_calendar AS
    WITH enrollment_data AS (
        SELECT
            ssa.studentusi,
            s.studentuniqueid,
            ssa.schoolid,
            ssa.calendarcode,
            CASE WHEN ssa.calendarcode = 'R' THEN 0 ELSE 1 END as calendar_flag,
            CASE WHEN ssa.schoolid < 100 OR ssa.schoolid::text LIKE '%Service%'
                 THEN 1 ELSE 0 END as serviceschool,
            CASE WHEN ssa.exitwithdrawtypedescriptorid IS NULL THEN 0 ELSE 1 END as withdrawal_flag,
            ssa.exitwithdrawdate,
            ssa.exitwithdrawtypedescriptorid,
            ssa.entrydate,
            ssa.entrygradeleveldescriptorid,
            ssa.schoolyear,
            ssa.lastmodifieddate,
            ROW_NUMBER() OVER (
                PARTITION BY ssa.studentusi
                ORDER BY
                    CASE WHEN ssa.calendarcode = 'R' THEN 0 ELSE 1 END,
                    CASE WHEN ssa.schoolid < 100 OR ssa.schoolid::text LIKE '%Service%' THEN 1 ELSE 0 END,
                    CASE WHEN ssa.exitwithdrawtypedescriptorid IS NULL THEN 0 ELSE 1 END,
                    ssa.entrydate DESC,
                    ssa.lastmodifieddate DESC
            ) as enrollment_priority
        FROM edfi.vw_studentschoolassociation ssa
        JOIN edfi.student s ON ssa.student_surrogateid = s.surrogateid
    )
    SELECT * FROM enrollment_data WHERE enrollment_priority = 1;

    CREATE INDEX idx_temp_enrollment_studentusi ON temp_enrollment_calendar(studentusi);
    temp_count := temp_count + 1;

    -- OPTIMIZATION 2: Combined student demographics with race and gender in one pass
    DROP TABLE IF EXISTS temp_student_demographics;
    CREATE TEMP TABLE temp_student_demographics AS
    SELECT
        seoa.studentusi,
        seoa.educationorganizationid,
        -- Gender with sort order
        MAX(CASE WHEN sd.descriptorid = seoa.sexdescriptorid
            THEN sd.codevalue END) as gender_code,
        MAX(CASE sd.codevalue
            WHEN 'Male' THEN 1
            WHEN 'Female' THEN 2
            ELSE 999 END) as gender_sort,
        -- Race determination using array aggregation for efficiency
        CASE
            WHEN bool_or(rd.codevalue = 'White') THEN 'White'
            WHEN bool_or(rd.codevalue = 'Black - African American') THEN 'Black'
            WHEN bool_or(rd.codevalue = 'Asian') THEN 'Asian'
            ELSE 'Other'
        END as race_code,
        -- Hispanic indicator
        CASE WHEN seoa.hispaniclatinoethnicity THEN 'Y' ELSE 'N' END as hispanic_ind
    FROM edfi.vw_studenteducationorganizationassociation seoa
    LEFT JOIN edfi.descriptor sd ON seoa.sexdescriptorid = sd.descriptorid
    LEFT JOIN edfi.studenteducationorganizationassociationrace seoar
        ON seoar.StudentEducationOrganizationAssociation_SurrogateId = seoa.surrogateid
    LEFT JOIN edfi.descriptor rd ON seoar.RaceDescriptor_SurrogateId = rd.surrogateid
    GROUP BY seoa.studentusi, seoa.educationorganizationid, seoa.hispaniclatinoethnicity;

    CREATE INDEX idx_temp_demographics_studentusi ON temp_student_demographics(studentusi);
    temp_count := temp_count + 1;

    -- OPTIMIZATION 3: Combined characteristics and indicators
    DROP TABLE IF EXISTS temp_characteristics_indicators;
    CREATE TEMP TABLE temp_characteristics_indicators AS
    SELECT
        seoa.studentusi,
        MAX(CASE WHEN cd.codevalue LIKE '%Homeless%' THEN 'Y' ELSE 'N' END) as homeless_ind,
        MAX(CASE WHEN cd.codevalue LIKE '%Migrant%' THEN 'Y' ELSE 'N' END) as migrant_ind,
        MAX(CASE WHEN cd.codevalue LIKE '%Military%' THEN 'Y' ELSE 'N' END) as military_ind,
        MAX(CASE WHEN cd.codevalue LIKE '%Economically%' THEN 'Y' ELSE 'N' END) as econ_disadvantaged_ind,
        COUNT(DISTINCT sc.studentcharacteristicdescriptor_surrogateid) as char_count,
        COUNT(DISTINCT si.surrogateid) as indicator_count
    FROM edfi.vw_studenteducationorganizationassociation seoa
    LEFT JOIN edfi.vw_studenteducationorganizationassociationstudentcharacteristic sc
        ON sc.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    LEFT JOIN edfi.descriptor cd
        ON cd.surrogateid = sc.studentcharacteristicdescriptor_surrogateid
    LEFT JOIN edfi.vw_studenteducationorganizationassociationstudentindicator si
        ON si.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    GROUP BY seoa.studentusi;

    CREATE INDEX idx_temp_characteristics_studentusi ON temp_characteristics_indicators(studentusi);
    temp_count := temp_count + 1;

    -- OPTIMIZATION 4: Programs and special education combined
    DROP TABLE IF EXISTS temp_programs_special_ed;
    CREATE TEMP TABLE temp_programs_special_ed AS
    WITH program_data AS (
        SELECT
            spa.studentusi,
            MAX(CASE WHEN pd.codevalue LIKE '%Special%' THEN 'Y' ELSE 'N' END) as disability_ind,
            MAX(CASE WHEN pd.codevalue LIKE '%Gifted%' THEN 'Y' ELSE 'N' END) as gifted_ind,
            MAX(CASE WHEN pd.codevalue LIKE '%ESL%' OR pd.codevalue LIKE '%English%' THEN 'Y' ELSE 'N' END) as esol_ind,
            MAX(CASE WHEN pd.codevalue LIKE '%LEP%' OR pd.codevalue LIKE '%Limited%' THEN 'Y' ELSE 'N' END) as lep_ind,
            MAX(CASE WHEN pd.codevalue LIKE '%Free%' OR pd.codevalue LIKE '%Reduced%' THEN
                CASE WHEN spa.begindate <= CURRENT_DATE AND
                     (gspa.enddate IS NULL OR gspa.enddate >= CURRENT_DATE)
                THEN 'Current' ELSE 'Historical' END
            END) as meal_status
        FROM edfi.vw_studentprogramassociation spa
        LEFT JOIN edfi.generalstudentprogramassociation gspa
            ON gspa.student_surrogateid = spa.student_surrogateid
            AND gspa.begindate = spa.begindate
            AND gspa.programtypedescriptorid = spa.programtypedescriptorid
        LEFT JOIN edfi.descriptor pd ON pd.descriptorid = spa.programtypedescriptorid
        GROUP BY spa.studentusi
    )
    SELECT * FROM program_data;

    CREATE INDEX idx_temp_programs_studentusi ON temp_programs_special_ed(studentusi);
    temp_count := temp_count + 1;

    -- OPTIMIZATION 5: Grade level with descriptor join
    DROP TABLE IF EXISTS temp_grade_level;
    CREATE TEMP TABLE temp_grade_level AS
    SELECT
        ec.studentusi,
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
    FROM temp_enrollment_calendar ec
    LEFT JOIN edfi.descriptor d ON ec.entrygradeleveldescriptorid = d.descriptorid;

    CREATE INDEX idx_temp_grade_studentusi ON temp_grade_level(studentusi);
    temp_count := temp_count + 1;

    -- FINAL OPTIMIZED QUERY: Single pass through student table with efficient joins
    RETURN QUERY
    SELECT
        -- Core Identifiers
        CAST(COALESCE(s.localeducationagencyid::text, '--') AS varchar(15)) as district_id,
        CAST(COALESCE(ec.schoolid::text, '--') AS varchar(15)) as eschool_building,
        CAST(COALESCE(RIGHT(ec.schoolid::text, 4), '--') AS varchar(30)) as school_id,
        CAST(COALESCE(ec.schoolyear, 2018) AS smallint) as school_year,
        CAST(COALESCE(st.studentuniqueid, '--') AS varchar(25)) as local_student_id,
        CAST(CASE WHEN ec.enrollment_priority = 1 AND ec.calendar_flag = 0 THEN 'Y' ELSE 'N' END AS char(1)) as current_enrolled_ind,
        COALESCE(ec.entrydate, DATE '1753-01-01') as entry_date_value,
        CAST(COALESCE(st.studentuniqueid, '--') AS varchar(25)) as unique_id,
        CAST(COALESCE(st.studentuniqueid, '--') AS varchar(25)) as lea_student_id,
        CAST(COALESCE(st.studentuniqueid, '--') AS varchar(25)) as state_id_nbr,
        CAST('--' AS varchar(15)) as ssn,
        CAST('-' AS char(1)) as exclude_from_reporting_ind,

        -- Name Information
        CAST(COALESCE(st.firstname, '--') AS varchar(35)) as first_name,
        CAST(COALESCE(st.middlename, '') AS varchar(15)) as middle_name,
        CAST(COALESCE(st.lastsurname, '--') AS varchar(35)) as last_name,
        CAST(COALESCE(st.personaltitleprefix, '') AS varchar(10)) as name_prefix,
        CAST(COALESCE(st.generationcodesuffix, '') AS varchar(10)) as name_suffix,
        CAST(COALESCE(TRIM(CONCAT_WS(' ', st.firstname, st.middlename, st.lastsurname, st.generationcodesuffix)), '--') AS varchar(80)) as full_name,
        CAST(COALESCE(st.firstname, '--') AS varchar(50)) as preferred_name,
        CAST(COALESCE(TRIM(CONCAT_WS(', ', CONCAT_WS(' ', st.lastsurname, st.generationcodesuffix), CONCAT_WS(' ', st.firstname, st.middlename))), '--') AS varchar(80)) as sort_name,

        -- Academic Information
        CAST('--' AS varchar(25)) as homeroom_nbr,
        CAST(0 AS smallint) as year_entered_9th_grade,
        CAST(COALESCE(ec.schoolid::text, '--') AS varchar(20)) as class_of,
        CAST('--' AS varchar(30)) as diploma_type_cd,
        CAST('--' AS varchar(254)) as diploma_type_desc,
        CAST(COALESCE(gl.grade_code, '--') AS varchar(30)) as grade_level_cd,
        CAST(COALESCE(gl.grade_code, '--') AS varchar(254)) as grade_level_desc,
        CAST(COALESCE(gl.grade_sort, 0) AS smallint) as grade_level_cd_sort_order,

        -- Demographics
        CAST(COALESCE(sd.gender_code, '--') AS varchar(30)) as gender_cd,
        CAST(COALESCE(sd.gender_code, '--') AS varchar(254)) as gender_desc,
        CAST(COALESCE(sd.gender_sort, 0) AS smallint) as gender_cd_sort_order,
        CAST(COALESCE(sd.race_code, '--') AS varchar(30)) as race_cd,
        CAST(COALESCE(sd.race_code, '--') AS varchar(254)) as race_desc,
        CAST(1 AS smallint) as race_cd_sort_order,
        COALESCE(st.birthdate, DATE '1753-01-01') as birth_date,
        CAST(COALESCE(st.birthcity, '--') AS varchar(80)) as place_of_birth,
        CAST('--' AS varchar(80)) as state_of_birth,
        CAST('--' AS varchar(80)) as country_of_birth,

        -- Student Characteristics
        CAST(CASE WHEN ci.migrant_ind = 'Y' THEN 'M' ELSE '--' END AS varchar(30)) as migrant_cd,
        CAST('--' AS varchar(254)) as migrant_desc,
        CAST('English' AS varchar(30)) as primary_language_cd,
        CAST('English' AS varchar(254)) as primary_language_desc,
        CAST('--' AS varchar(30)) as english_proficiency_cd,
        CAST('--' AS varchar(254)) as english_proficiency_desc,
        CAST(COALESCE(pse.meal_status, '--') AS varchar(30)) as free_reduced_meal_cd,
        CAST(COALESCE(pse.meal_status, '--') AS varchar(254)) as free_reduced_meal_desc,
        CAST('--' AS varchar(30)) as primary_disability_cd,
        CAST('--' AS varchar(254)) as primary_disability_desc,

        -- Indicator Flags
        CAST(COALESCE(pse.disability_ind, 'N') AS char(1)) as disability_ind,
        CAST(COALESCE(ci.econ_disadvantaged_ind, 'N') AS char(1)) as economically_disadvantaged_ind,
        CAST(COALESCE(pse.esol_ind, 'N') AS char(1)) as esol_ind,
        CAST(COALESCE(pse.gifted_ind, 'N') AS char(1)) as gifted_ind,
        CAST(COALESCE(pse.lep_ind, 'N') AS char(1)) as limited_english_proficiency_ind,
        CAST(COALESCE(ci.migrant_ind, 'N') AS char(1)) as migrant_ind,
        CAST(COALESCE(ci.homeless_ind, 'N') AS char(1)) as homeless_ind,
        CAST(COALESCE(ci.military_ind, 'N') AS char(1)) as military_connected_ind,

        -- Race/Ethnicity Individual Indicators
        CAST(CASE WHEN sd.race_code = 'White' THEN 'Y' ELSE 'N' END AS char(1)) as white_ind,
        CAST(CASE WHEN sd.race_code = 'Black' THEN 'Y' ELSE 'N' END AS char(1)) as black_african_american_ind,
        CAST(CASE WHEN sd.race_code = 'Asian' THEN 'Y' ELSE 'N' END AS char(1)) as asian_ind,
        CAST('N' AS char(1)) as hawaiian_pacific_islander_ind,
        CAST('N' AS char(1)) as american_indian_alaskan_native_ind,
        CAST('N' AS char(1)) as two_or_more_races_ind,
        CAST(COALESCE(sd.hispanic_ind, 'N') AS char(1)) as hispanic_latino_ethnicity_ind,

        -- Performance metrics
        temp_count,
        CAST(EXTRACT(epoch FROM (clock_timestamp() - start_time)) * 1000 AS bigint) as processing_time_ms
    FROM edfi.student st
    INNER JOIN temp_enrollment_calendar ec ON ec.studentusi = st.studentusi
    LEFT JOIN edfi.school s ON s.schoolid = ec.schoolid
    LEFT JOIN temp_student_demographics sd ON sd.studentusi = st.studentusi
    LEFT JOIN temp_characteristics_indicators ci ON ci.studentusi = st.studentusi
    LEFT JOIN temp_programs_special_ed pse ON pse.studentusi = st.studentusi
    LEFT JOIN temp_grade_level gl ON gl.studentusi = st.studentusi
    WHERE (p_Batch_Period_List = 'all'
           OR p_Batch_Period_List IS NULL
           OR ec.schoolyear::varchar IN (SELECT batch_period FROM temp_batch_periods))
    ORDER BY ec.enrollment_priority, ec.calendar_flag, ec.serviceschool, st.studentuniqueid;

END;
$function$;