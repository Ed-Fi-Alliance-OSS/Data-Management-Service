-- Complete Optimized PostgreSQL version of sp_iMart_Transform_DIM_STUDENT_edfi_Postgres
-- Adapted for denormalized database with surrogate keys
CREATE OR REPLACE FUNCTION sp_imart_transform_dim_student_edfi_postgres_original_optimized(
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

    -- CLEANUP: Drop temp tables from previous runs
    DROP TABLE IF EXISTS temp_batch_periods CASCADE;
    DROP TABLE IF EXISTS temp_enrollment_calendar CASCADE;
    DROP TABLE IF EXISTS temp_student_demographics CASCADE;
    DROP TABLE IF EXISTS temp_characteristics_indicators CASCADE;
    DROP TABLE IF EXISTS temp_programs_special_ed CASCADE;
    DROP TABLE IF EXISTS temp_grade_level CASCADE;

    -- 1. Batch period processing
    CREATE TEMP TABLE temp_batch_periods (
        batch_period varchar(50) PRIMARY KEY
    );

    IF p_Batch_Period_List IS NOT NULL AND LENGTH(TRIM(p_Batch_Period_List)) > 0 AND p_Batch_Period_List != 'all' THEN
        INSERT INTO temp_batch_periods (batch_period)
        SELECT TRIM(unnest(string_to_array(p_Batch_Period_List, ',')));
    END IF;
    temp_count := temp_count + 1;

    -- 2. Enrollment and Calendar Data with Priority
    CREATE TEMP TABLE temp_enrollment_calendar AS
    WITH enrollment_priority AS (
        SELECT
            s.studentusi,
            s.studentuniqueid,
            s.firstname,
            s.middlename,
            s.lastsurname,
            s.personaltitleprefix,
            s.generationcodesuffix,
            s.birthdate,
            s.birthcity,
            sc.schoolid,
            sc.localeducationagencyid,
            ssa.calendarcode,
            ssa.schoolyear,
            CASE WHEN ssa.calendarcode = 'R' THEN 0 ELSE 1 END as calendar_flag,
            CASE WHEN sc.schoolid < 100 OR sc.schoolid::text LIKE '%Service%' THEN 1 ELSE 0 END as serviceschool,
            CASE WHEN ssa.exitwithdrawtypedescriptorid IS NULL THEN 0 ELSE 1 END as withdrawal_flag,
            ssa.entrydate,
            ssa.exitwithdrawdate,
            ssa.entrygradeleveldescriptorid,
            ROW_NUMBER() OVER (
                PARTITION BY s.studentusi
                ORDER BY
                    CASE WHEN ssa.calendarcode = 'R' THEN 0 ELSE 1 END,
                    CASE WHEN sc.schoolid < 100 OR sc.schoolid::text LIKE '%Service%' THEN 1 ELSE 0 END,
                    CASE WHEN ssa.exitwithdrawtypedescriptorid IS NULL THEN 0 ELSE 1 END,
                    ssa.entrydate DESC,
                    ssa.lastmodifieddate DESC
            ) as enrollment_priority
        FROM edfi.studentschoolassociation ssa
        INNER JOIN edfi.student s ON s.surrogateid = ssa.student_surrogateid
        INNER JOIN edfi.school sc ON sc.surrogateid = ssa.school_surrogateid
    )
    SELECT * FROM enrollment_priority WHERE enrollment_priority = 1;

    CREATE INDEX idx_temp_enrollment_studentusi ON temp_enrollment_calendar(studentusi);
    temp_count := temp_count + 1;

    -- 3. Student Demographics (Gender and Race)
    CREATE TEMP TABLE temp_student_demographics AS
    SELECT
        s.studentusi,
        MAX(CASE WHEN gd.codevalue IS NOT NULL THEN gd.codevalue ELSE '--' END) as gender_code,
        MAX(CASE
            WHEN gd.codevalue = 'Male' THEN 1
            WHEN gd.codevalue = 'Female' THEN 2
            ELSE 999
        END) as gender_sort,
        -- Race determination with boolean aggregation
        CASE
            WHEN bool_or(rd.codevalue = 'White') AND NOT bool_or(rd.codevalue != 'White') THEN 'White'
            WHEN bool_or(rd.codevalue = 'Black - African American') AND NOT bool_or(rd.codevalue NOT IN ('Black - African American')) THEN 'Black'
            WHEN bool_or(rd.codevalue = 'Asian') AND NOT bool_or(rd.codevalue != 'Asian') THEN 'Asian'
            WHEN bool_or(rd.codevalue = 'American Indian - Alaska Native') THEN 'American Indian'
            WHEN bool_or(rd.codevalue = 'Native Hawaiian - Pacific Islander') THEN 'Pacific Islander'
            WHEN COUNT(DISTINCT rd.codevalue) > 1 THEN 'Two or More'
            ELSE COALESCE(MAX(rd.codevalue), 'Unknown')
        END as race_code,
        bool_or(rd.codevalue = 'White') as white_ind,
        bool_or(rd.codevalue = 'Black - African American') as black_ind,
        bool_or(rd.codevalue = 'Asian') as asian_ind,
        bool_or(rd.codevalue = 'Native Hawaiian - Pacific Islander') as hawaiian_pacific_ind,
        bool_or(rd.codevalue = 'American Indian - Alaska Native') as american_indian_ind,
        CASE WHEN COUNT(DISTINCT rd.codevalue) > 1 THEN true ELSE false END as two_or_more_ind,
        COALESCE(bool_or(seoa.hispaniclatinoethnicity), false) as hispanic_latino_ind
    FROM edfi.student s
    INNER JOIN temp_enrollment_calendar tec ON tec.studentusi = s.studentusi
    LEFT JOIN edfi.studenteducationorganizationassociation seoa
        ON seoa.student_surrogateid = s.surrogateid
    LEFT JOIN edfi.school sc ON sc.schoolid = tec.schoolid
    LEFT JOIN edfi.descriptor gd ON seoa.sexdescriptorid = gd.descriptorid
    LEFT JOIN edfi.studenteducationorganizationassociationrace seoar
        ON seoar.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    LEFT JOIN edfi.descriptor rd ON seoar.racedescriptor_surrogateid = rd.surrogateid
    GROUP BY s.studentusi;

    CREATE INDEX idx_temp_demographics_studentusi ON temp_student_demographics(studentusi);
    temp_count := temp_count + 1;

    -- 4. Characteristics and Indicators
    CREATE TEMP TABLE temp_characteristics_indicators AS
    SELECT
        s.studentusi,
        MAX(CASE WHEN scd.codevalue LIKE '%Homeless%' THEN 'Y' ELSE 'N' END) as homeless_ind,
        MAX(CASE WHEN scd.codevalue LIKE '%Migrant%' THEN 'Y' ELSE 'N' END) as migrant_ind,
        MAX(CASE WHEN scd.codevalue LIKE '%Military%' THEN 'Y' ELSE 'N' END) as military_ind,
        MAX(CASE WHEN scd.codevalue LIKE '%Economically Disadvantaged%' THEN 'Y' ELSE 'N' END) as economically_disadvantaged_ind,
        COUNT(DISTINCT scd.codevalue) as characteristic_count
    FROM edfi.student s
    INNER JOIN temp_enrollment_calendar tec ON tec.studentusi = s.studentusi
    LEFT JOIN edfi.studenteducationorganizationassociation seoa
        ON seoa.student_surrogateid = s.surrogateid
    LEFT JOIN edfi.studenteducationorganizationassociationstudentcharacteristic seoasc
        ON seoasc.studenteducationorganizationassociation_surrogateid = seoa.surrogateid
    LEFT JOIN edfi.descriptor scd ON seoasc.studentcharacteristicdescriptor_surrogateid = scd.surrogateid
    GROUP BY s.studentusi;

    CREATE INDEX idx_temp_characteristics_studentusi ON temp_characteristics_indicators(studentusi);
    temp_count := temp_count + 1;

    -- 5. Program Associations and Special Education
    CREATE TEMP TABLE temp_programs_special_ed AS
    WITH program_data AS (
        SELECT
            s.studentusi,
            MAX(CASE WHEN pd.codevalue LIKE '%Special Education%' THEN 'Y' ELSE 'N' END) as disability_ind,
            MAX(CASE WHEN pd.codevalue LIKE '%Gifted%' THEN 'Y' ELSE 'N' END) as gifted_ind,
            MAX(CASE WHEN pd.codevalue LIKE '%English Language%' OR pd.codevalue LIKE '%ESL%' THEN 'Y' ELSE 'N' END) as esol_ind,
            MAX(CASE WHEN pd.codevalue LIKE '%Limited English%' THEN 'Y' ELSE 'N' END) as lep_ind,
            MAX(CASE
                WHEN fsd.codevalue = 'Free' THEN 'F'
                WHEN fsd.codevalue = 'Reduced' OR fsd.codevalue = 'Reduced price' THEN 'R'
                ELSE '--'
            END) as meal_status
        FROM edfi.student s
        INNER JOIN temp_enrollment_calendar tec ON tec.studentusi = s.studentusi
        LEFT JOIN edfi.studentprogramassociation spa ON spa.student_surrogateid = s.surrogateid
        LEFT JOIN edfi.program p ON p.programname = spa.programname
            AND p.programtypedescriptorid = spa.programtypedescriptorid
        LEFT JOIN edfi.descriptor pd ON spa.programtypedescriptorid = pd.descriptorid
        LEFT JOIN edfi.studentschoolfoodserviceprogramassociation sfpa ON sfpa.studentusi = s.studentusi
        LEFT JOIN edfi.descriptor fsd ON sfpa.directcertification = true
        GROUP BY s.studentusi
    )
    SELECT * FROM program_data;

    CREATE INDEX idx_temp_programs_studentusi ON temp_programs_special_ed(studentusi);
    temp_count := temp_count + 1;

    -- 6. Grade Level Processing
    CREATE TEMP TABLE temp_grade_level AS
    SELECT DISTINCT ON (tec.studentusi)
        tec.studentusi,
        COALESCE(gld.codevalue, '--') as grade_code,
        CASE gld.codevalue
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
    FROM temp_enrollment_calendar tec
    LEFT JOIN edfi.descriptor gld ON tec.entrygradeleveldescriptorid = gld.descriptorid
    ORDER BY tec.studentusi, tec.enrollment_priority;

    CREATE INDEX idx_temp_grade_studentusi ON temp_grade_level(studentusi);
    temp_count := temp_count + 1;

    -- FINAL QUERY: Assemble all data
    RETURN QUERY
    SELECT
        -- Core Identifiers
        CAST(COALESCE(tec.localeducationagencyid::text, '--') AS varchar(15)) as district_id,
        CAST(COALESCE(tec.schoolid::text, '--') AS varchar(15)) as eschool_building,
        CAST(COALESCE(RIGHT(tec.schoolid::text, 4), '--') AS varchar(30)) as school_id,
        CAST(COALESCE(tec.schoolyear, 2018) AS smallint) as school_year,
        CAST(COALESCE(tec.studentuniqueid, '--') AS varchar(25)) as local_student_id,
        CAST(CASE
            WHEN tec.enrollment_priority = 1 AND tec.calendar_flag = 0 AND tec.withdrawal_flag = 0
            THEN 'Y' ELSE 'N'
        END AS char(1)) as current_enrolled_ind,
        COALESCE(tec.entrydate, DATE '1753-01-01') as entry_date_value,
        CAST(COALESCE(tec.studentuniqueid, '--') AS varchar(25)) as unique_id,
        CAST(COALESCE(tec.studentuniqueid, '--') AS varchar(25)) as lea_student_id,
        CAST(COALESCE(tec.studentuniqueid, '--') AS varchar(25)) as state_id_nbr,
        CAST('--' AS varchar(15)) as ssn,
        CAST('-' AS char(1)) as exclude_from_reporting_ind,

        -- Name Information
        CAST(COALESCE(tec.firstname, '--') AS varchar(35)) as first_name,
        CAST(COALESCE(tec.middlename, '') AS varchar(15)) as middle_name,
        CAST(COALESCE(tec.lastsurname, '--') AS varchar(35)) as last_name,
        CAST(COALESCE(tec.personaltitleprefix, '') AS varchar(10)) as name_prefix,
        CAST(COALESCE(tec.generationcodesuffix, '') AS varchar(10)) as name_suffix,
        CAST(COALESCE(TRIM(
            COALESCE(tec.firstname, '') || ' ' ||
            COALESCE(tec.middlename, '') || ' ' ||
            COALESCE(tec.lastsurname, '') || ' ' ||
            COALESCE(tec.generationcodesuffix, '')
        ), '--') AS varchar(80)) as full_name,
        CAST(COALESCE(tec.firstname, '--') AS varchar(50)) as preferred_name,
        CAST(COALESCE(TRIM(
            COALESCE(tec.lastsurname, '') || ' ' ||
            COALESCE(tec.generationcodesuffix, '') || ', ' ||
            COALESCE(tec.firstname, '') || ' ' ||
            COALESCE(tec.middlename, '')
        ), '--') AS varchar(80)) as sort_name,

        -- Academic Information
        CAST('--' AS varchar(25)) as homeroom_nbr,
        CAST(0 AS smallint) as year_entered_9th_grade,
        CAST(COALESCE(tec.schoolid::text, '--') AS varchar(20)) as class_of,
        CAST('--' AS varchar(30)) as diploma_type_cd,
        CAST('--' AS varchar(254)) as diploma_type_desc,
        CAST(COALESCE(tgl.grade_code, '--') AS varchar(30)) as grade_level_cd,
        CAST(COALESCE(tgl.grade_code, '--') AS varchar(254)) as grade_level_desc,
        CAST(COALESCE(tgl.grade_sort, 999) AS smallint) as grade_level_cd_sort_order,

        -- Demographics
        CAST(COALESCE(tsd.gender_code, '--') AS varchar(30)) as gender_cd,
        CAST(COALESCE(tsd.gender_code, '--') AS varchar(254)) as gender_desc,
        CAST(COALESCE(tsd.gender_sort, 999) AS smallint) as gender_cd_sort_order,
        CAST(COALESCE(tsd.race_code, '--') AS varchar(30)) as race_cd,
        CAST(COALESCE(tsd.race_code, '--') AS varchar(254)) as race_desc,
        CAST(CASE tsd.race_code
            WHEN 'White' THEN 1
            WHEN 'Black' THEN 2
            WHEN 'Asian' THEN 3
            WHEN 'American Indian' THEN 4
            WHEN 'Pacific Islander' THEN 5
            WHEN 'Two or More' THEN 6
            ELSE 999
        END AS smallint) as race_cd_sort_order,
        COALESCE(tec.birthdate, DATE '1753-01-01') as birth_date,
        CAST(COALESCE(tec.birthcity, '--') AS varchar(80)) as place_of_birth,
        CAST('--' AS varchar(80)) as state_of_birth,
        CAST('--' AS varchar(80)) as country_of_birth,

        -- Student Characteristics
        CAST(CASE WHEN tci.migrant_ind = 'Y' THEN 'M' ELSE '--' END AS varchar(30)) as migrant_cd,
        CAST(CASE WHEN tci.migrant_ind = 'Y' THEN 'Migrant' ELSE '--' END AS varchar(254)) as migrant_desc,
        CAST('English' AS varchar(30)) as primary_language_cd,
        CAST('English' AS varchar(254)) as primary_language_desc,
        CAST(CASE WHEN tpse.lep_ind = 'Y' THEN 'LEP' ELSE '--' END AS varchar(30)) as english_proficiency_cd,
        CAST(CASE WHEN tpse.lep_ind = 'Y' THEN 'Limited English Proficiency' ELSE '--' END AS varchar(254)) as english_proficiency_desc,
        CAST(COALESCE(tpse.meal_status, '--') AS varchar(30)) as free_reduced_meal_cd,
        CAST(CASE tpse.meal_status
            WHEN 'F' THEN 'Free'
            WHEN 'R' THEN 'Reduced'
            ELSE '--'
        END AS varchar(254)) as free_reduced_meal_desc,
        CAST(CASE WHEN tpse.disability_ind = 'Y' THEN 'SPED' ELSE '--' END AS varchar(30)) as primary_disability_cd,
        CAST(CASE WHEN tpse.disability_ind = 'Y' THEN 'Special Education' ELSE '--' END AS varchar(254)) as primary_disability_desc,

        -- Indicator Flags
        CAST(COALESCE(tpse.disability_ind, 'N') AS char(1)) as disability_ind,
        CAST(COALESCE(tci.economically_disadvantaged_ind, 'N') AS char(1)) as economically_disadvantaged_ind,
        CAST(COALESCE(tpse.esol_ind, 'N') AS char(1)) as esol_ind,
        CAST(COALESCE(tpse.gifted_ind, 'N') AS char(1)) as gifted_ind,
        CAST(COALESCE(tpse.lep_ind, 'N') AS char(1)) as limited_english_proficiency_ind,
        CAST(COALESCE(tci.migrant_ind, 'N') AS char(1)) as migrant_ind,
        CAST(COALESCE(tci.homeless_ind, 'N') AS char(1)) as homeless_ind,
        CAST(COALESCE(tci.military_ind, 'N') AS char(1)) as military_connected_ind,

        -- Race/Ethnicity Individual Indicators
        CAST(CASE WHEN tsd.white_ind THEN 'Y' ELSE 'N' END AS char(1)) as white_ind,
        CAST(CASE WHEN tsd.black_ind THEN 'Y' ELSE 'N' END AS char(1)) as black_african_american_ind,
        CAST(CASE WHEN tsd.asian_ind THEN 'Y' ELSE 'N' END AS char(1)) as asian_ind,
        CAST(CASE WHEN tsd.hawaiian_pacific_ind THEN 'Y' ELSE 'N' END AS char(1)) as hawaiian_pacific_islander_ind,
        CAST(CASE WHEN tsd.american_indian_ind THEN 'Y' ELSE 'N' END AS char(1)) as american_indian_alaskan_native_ind,
        CAST(CASE WHEN tsd.two_or_more_ind THEN 'Y' ELSE 'N' END AS char(1)) as two_or_more_races_ind,
        CAST(CASE WHEN tsd.hispanic_latino_ind THEN 'Y' ELSE 'N' END AS char(1)) as hispanic_latino_ethnicity_ind,

        -- Performance metrics
        temp_count,
        CAST(EXTRACT(epoch FROM (clock_timestamp() - start_time)) * 1000 AS bigint) as processing_time_ms

    FROM temp_enrollment_calendar tec
    LEFT JOIN temp_student_demographics tsd ON tsd.studentusi = tec.studentusi
    LEFT JOIN temp_characteristics_indicators tci ON tci.studentusi = tec.studentusi
    LEFT JOIN temp_programs_special_ed tpse ON tpse.studentusi = tec.studentusi
    LEFT JOIN temp_grade_level tgl ON tgl.studentusi = tec.studentusi
    WHERE (p_Batch_Period_List = 'all'
           OR p_Batch_Period_List IS NULL
           OR tec.schoolyear::varchar IN (SELECT batch_period FROM temp_batch_periods))
    ORDER BY tec.studentusi;

END;
$function$;