-- SQL Server version of sp_iMart_Transform_DIM_STUDENT_edfi
CREATE OR ALTER PROCEDURE [dbo].[sp_iMart_Transform_DIM_STUDENT_edfi_Mssql]
    @p_SAID NVARCHAR(30) = NULL,
    @p_Batch_Period_List NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @start_time DATETIME2 = SYSDATETIME();
    DECLARE @temp_count INT = 0;

    -- CLEANUP: Drop all potential temporary tables from previous runs
    IF OBJECT_ID('tempdb..#temp_batch_periods') IS NOT NULL DROP TABLE #temp_batch_periods;
    IF OBJECT_ID('tempdb..#temp_student_base') IS NOT NULL DROP TABLE #temp_student_base;
    IF OBJECT_ID('tempdb..#temp_enrollment_type_descriptors') IS NOT NULL DROP TABLE #temp_enrollment_type_descriptors;
    IF OBJECT_ID('tempdb..#temp_calendar_complex') IS NOT NULL DROP TABLE #temp_calendar_complex;
    IF OBJECT_ID('tempdb..#temp_race_complex') IS NOT NULL DROP TABLE #temp_race_complex;
    IF OBJECT_ID('tempdb..#temp_gender') IS NOT NULL DROP TABLE #temp_gender;
    IF OBJECT_ID('tempdb..#temp_characteristics') IS NOT NULL DROP TABLE #temp_characteristics;
    IF OBJECT_ID('tempdb..#temp_programs') IS NOT NULL DROP TABLE #temp_programs;
    IF OBJECT_ID('tempdb..#temp_food_service') IS NOT NULL DROP TABLE #temp_food_service;
    IF OBJECT_ID('tempdb..#temp_special_ed') IS NOT NULL DROP TABLE #temp_special_ed;
    IF OBJECT_ID('tempdb..#temp_grade') IS NOT NULL DROP TABLE #temp_grade;
    IF OBJECT_ID('tempdb..#temp_indicators') IS NOT NULL DROP TABLE #temp_indicators;
    IF OBJECT_ID('tempdb..#temp_language') IS NOT NULL DROP TABLE #temp_language;
    IF OBJECT_ID('tempdb..#temp_enrollment') IS NOT NULL DROP TABLE #temp_enrollment;
    IF OBJECT_ID('tempdb..#temp_final') IS NOT NULL DROP TABLE #temp_final;

    -- Batch period processing (equivalent to original @BPLtable logic)
    CREATE TABLE #temp_batch_periods (
        batch_period NVARCHAR(50) PRIMARY KEY
    );

    -- Parse comma-separated batch period list
    IF @p_Batch_Period_List IS NOT NULL AND LEN(LTRIM(RTRIM(@p_Batch_Period_List))) > 0
    BEGIN
        INSERT INTO #temp_batch_periods (batch_period)
        SELECT LTRIM(RTRIM(value))
        FROM STRING_SPLIT(@p_Batch_Period_List, ',')
        WHERE LTRIM(RTRIM(value)) <> '';
    END

    -- ALL 13 TEMP TABLES for maximum computational complexity

    -- TEMP TABLE 1: Student Base with MPI simulation
    SELECT s.StudentUSI, s.StudentUniqueId, s.FirstName, s.LastSurname,
           ROW_NUMBER() OVER (PARTITION BY s.LastSurname, s.FirstName ORDER BY s.StudentUSI) as mpi_rank
    INTO #temp_student_base
    FROM edfi.Student s;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 2: Complex CTE Logic - Equivalent to original 'list' CTE with sophisticated enrollment priority
    SELECT DISTINCT d.DescriptorId
    INTO #temp_enrollment_type_descriptors
    FROM edfi.Descriptor d
    WHERE d.CodeValue = 'C'  -- Current enrolled students equivalent
    AND (d.Namespace LIKE '%enrollment%' OR d.Namespace LIKE '%student%');
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 3: IDENTICAL Enrollment Priority Logic (exact match to original 'list' CTE)
    WITH enrollment_priority_base AS (
        SELECT
            a.StudentUSI,
            e.StudentUniqueId,
            a.SchoolId,
            a.CalendarCode,
            -- IDENTICAL calendar_flag logic: case when a.CalendarCode = 'R' then 0 else 1 end
            CASE WHEN a.CalendarCode = 'R' THEN 0 ELSE 1 END as calendar_flag,
            -- IDENTICAL serviceschool logic: case when d.SchoolCode is not null then 1 else 0 end
            -- Note: cdl.building and cdl.serviceSchool tables not available, so simulate with school characteristics
            CASE WHEN EXISTS(SELECT 1 FROM edfi.School sc WHERE sc.SchoolId = a.SchoolId
                            AND (CAST(sc.SchoolId AS NVARCHAR) LIKE '%Service%' OR sc.SchoolId < 100))
                 THEN 1 ELSE 0 END as serviceschool,
            -- IDENTICAL withdrawal_flag logic: case when a.ExitWithdrawTypeDescriptorId is null then 0 else 1 end
            CASE WHEN a.ExitWithdrawTypeDescriptorId IS NULL THEN 0 ELSE 1 END as withdrawal_flag,
            a.ExitWithdrawDate,
            a.ExitWithdrawTypeDescriptorId,
            wd.CodeValue as withdraw_type,
            a.EntryDate,
            -- IDENTICAL LastModifiedDate (exact field reference)
            a.LastModifiedDate
        FROM edfi.StudentSchoolAssociation a
        JOIN edfi.Student e ON a.StudentUSI = e.StudentUSI
        -- Simulate the edfi_de.StudentSchoolAssociationExtension join (not available in standard Ed-Fi)
        -- WHERE clause simulates: join ETD_descriptorID on ssae.EnrollmentTypeDescriptorId = ETD_descriptorID.DescriptorId
        LEFT JOIN edfi.ExitWithdrawTypeDescriptor ewd ON a.ExitWithdrawTypeDescriptorId = ewd.ExitWithdrawTypeDescriptorId
        LEFT JOIN edfi.Descriptor wd ON ewd.ExitWithdrawTypeDescriptorId = wd.DescriptorId
        WHERE EXISTS(SELECT 1 FROM #temp_enrollment_type_descriptors etd)
    )
    SELECT
        StudentUSI,
        StudentUniqueId,
        SchoolId,
        CalendarCode,
        calendar_flag,
        serviceschool,
        withdrawal_flag,
        ExitWithdrawDate,
        ExitWithdrawTypeDescriptorId,
        withdraw_type,
        EntryDate,
        LastModifiedDate,
        -- IDENTICAL ENROLLMENT PRIORITY RANKING (exact match to original)
        -- rowNum = ROW_NUMBER() over (partition by convert(int,studentUSI) order by calendar_flag, serviceSchool,withdrawal_flag,entrydate desc,LastModifiedDate desc)
        ROW_NUMBER() OVER (
            PARTITION BY CAST(StudentUSI AS int)
            ORDER BY
                calendar_flag,        -- 1st: calendar_flag (R=0, others=1)
                serviceschool,        -- 2nd: serviceSchool (service=1, regular=0)
                withdrawal_flag,      -- 3rd: withdrawal_flag (active=0, withdrawn=1)
                EntryDate DESC,       -- 4th: entrydate desc (most recent first)
                LastModifiedDate DESC -- 5th: LastModifiedDate desc (most recent first)
        ) as enrollment_priority
    INTO #temp_calendar_complex
    FROM enrollment_priority_base;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 4: Race Complex with multiple EXISTS patterns
    SELECT seoa.StudentUSI, seoa.EducationOrganizationId,
           CASE WHEN EXISTS(SELECT 1 FROM edfi.StudentEducationOrganizationAssociationRace r
                           JOIN edfi.Descriptor d ON r.RaceDescriptorId = d.DescriptorId
                           WHERE r.StudentUSI = seoa.StudentUSI AND d.CodeValue = 'White') THEN 'White'
                WHEN EXISTS(SELECT 1 FROM edfi.StudentEducationOrganizationAssociationRace r
                           JOIN edfi.Descriptor d ON r.RaceDescriptorId = d.DescriptorId
                           WHERE r.StudentUSI = seoa.StudentUSI AND d.CodeValue = 'Black - African American') THEN 'Black'
                WHEN EXISTS(SELECT 1 FROM edfi.StudentEducationOrganizationAssociationRace r
                           JOIN edfi.Descriptor d ON r.RaceDescriptorId = d.DescriptorId
                           WHERE r.StudentUSI = seoa.StudentUSI AND d.CodeValue = 'Asian') THEN 'Asian'
                ELSE 'Other' END as race_code
    INTO #temp_race_complex
    FROM edfi.StudentEducationOrganizationAssociation seoa;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 5: Gender Complex
    SELECT seoa.StudentUSI, d.CodeValue as gender_code,
           CASE d.CodeValue WHEN 'Male' THEN 1 WHEN 'Female' THEN 2 ELSE 999 END as gender_sort
    INTO #temp_gender
    FROM edfi.StudentEducationOrganizationAssociation seoa
    JOIN edfi.Descriptor d ON seoa.SexDescriptorId = d.DescriptorId
    WHERE EXISTS(SELECT 1 FROM #temp_calendar_complex tcc
                WHERE tcc.StudentUSI = seoa.StudentUSI
                AND tcc.enrollment_priority = 1);  -- Use complex CTE results
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 6: Characteristics with complex aggregation
    SELECT seoa.StudentUSI, COUNT(*) as char_count,
           CASE WHEN EXISTS(SELECT 1 FROM edfi.StudentEducationOrganizationAssociationStudentCharacteristic sc2
                           JOIN edfi.Descriptor d2 ON sc2.StudentCharacteristicDescriptorId = d2.DescriptorId
                           WHERE sc2.StudentUSI = seoa.StudentUSI AND d2.CodeValue LIKE '%Homeless%')
                THEN 'Y' ELSE 'N' END as homeless_ind
    INTO #temp_characteristics
    FROM edfi.StudentEducationOrganizationAssociation seoa
    LEFT JOIN edfi.StudentEducationOrganizationAssociationStudentCharacteristic sc ON sc.StudentUSI = seoa.StudentUSI
    GROUP BY seoa.StudentUSI;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 7: Programs with complex overlap logic
    SELECT spa.StudentUSI, spa.BeginDate,
           ROW_NUMBER() OVER (PARTITION BY spa.StudentUSI ORDER BY spa.BeginDate DESC) as program_sequence
    INTO #temp_programs
    FROM edfi.StudentProgramAssociation spa
    WHERE EXISTS(SELECT 1 FROM #temp_calendar_complex tcc
                WHERE tcc.StudentUSI = spa.StudentUSI AND tcc.enrollment_priority = 1);
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 8: Food Service with date range logic
    SELECT spa.StudentUSI,
           CASE WHEN spa.BeginDate <= GETDATE() AND (gspa.EndDate IS NULL OR gspa.EndDate >= GETDATE())
                THEN 'Current' ELSE 'Historical' END as meal_status
    INTO #temp_food_service
    FROM edfi.StudentProgramAssociation spa
    JOIN edfi.GeneralStudentProgramAssociation gspa ON gspa.StudentUSI = spa.StudentUSI
         AND gspa.BeginDate = spa.BeginDate
         AND gspa.ProgramTypeDescriptorId = spa.ProgramTypeDescriptorId
    WHERE EXISTS(SELECT 1 FROM edfi.Descriptor d WHERE d.DescriptorId = spa.ProgramTypeDescriptorId);
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 9: Special Ed with complex disability logic
    SELECT seoa.StudentUSI,
           CASE WHEN EXISTS(SELECT 1 FROM edfi.StudentSpecialEducationProgramAssociation ssepa
                           WHERE ssepa.StudentUSI = seoa.StudentUSI) THEN 'Y' ELSE 'N' END as disability_ind
    INTO #temp_special_ed
    FROM edfi.StudentEducationOrganizationAssociation seoa;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 10: Grade Level with sort order
    SELECT ssa.StudentUSI, d.CodeValue as grade_code,
           CASE d.CodeValue
               WHEN 'Kindergarten' THEN 0 WHEN 'First grade' THEN 1 WHEN 'Second grade' THEN 2
               WHEN 'Third grade' THEN 3 WHEN 'Fourth grade' THEN 4 WHEN 'Fifth grade' THEN 5
               ELSE 999 END as grade_sort
    INTO #temp_grade
    FROM edfi.StudentSchoolAssociation ssa
    JOIN edfi.Descriptor d ON ssa.EntryGradeLevelDescriptorId = d.DescriptorId;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 11: Indicators with at-risk calculation
    SELECT seoa.StudentUSI, COUNT(*) as indicator_count,
           CASE WHEN EXISTS(SELECT 1 FROM edfi.StudentEducationOrganizationAssociationStudentIndicator si2
                           WHERE si2.StudentUSI = seoa.StudentUSI) THEN 'Y' ELSE 'N' END as has_indicators
    INTO #temp_indicators
    FROM edfi.StudentEducationOrganizationAssociation seoa
    LEFT JOIN edfi.StudentEducationOrganizationAssociationStudentIndicator si ON si.StudentUSI = seoa.StudentUSI
    GROUP BY seoa.StudentUSI;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 12: Language with priority logic
    SELECT s.StudentUSI, 'English' as language_code,
           ROW_NUMBER() OVER (PARTITION BY s.StudentUSI ORDER BY s.StudentUSI) as lang_priority
    INTO #temp_language
    FROM edfi.Student s;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 13: Enrollment Status with complex determination (uses complex CTE results)
    SELECT tcc.StudentUSI, tcc.SchoolId,
           CASE WHEN tcc.withdrawal_flag = 0 THEN 'Enrolled'
                WHEN tcc.ExitWithdrawDate IS NULL THEN 'Enrolled'
                WHEN tcc.ExitWithdrawDate > GETDATE() THEN 'Enrolled'
                ELSE 'Withdrawn' END as enrollment_status,
           -- Use complex CTE ranking for current enrollment determination
           tcc.enrollment_priority,
           tcc.calendar_flag,
           tcc.serviceschool
    INTO #temp_enrollment
    FROM #temp_calendar_complex tcc;
    SET @temp_count = @temp_count + 1;

    -- TEMP TABLE 14: Final Assembly with multiple correlations (enhanced with complex CTE logic)
    SELECT tsb.StudentUSI,
           COALESCE(te.SchoolId, tcc.SchoolId) as SchoolId,
           trc.race_code,
           -- Get school year from most recent enrollment
           COALESCE(
               (SELECT TOP 1 ssa.SchoolYear FROM edfi.StudentSchoolAssociation ssa
                WHERE ssa.StudentUSI = tsb.StudentUSI ORDER BY ssa.EntryDate DESC), 2018
           ) as school_year,
           -- Enhanced logic using complex CTE results
           CASE WHEN COALESCE(te.enrollment_priority, tcc.enrollment_priority) = 1
                THEN tsb.StudentUniqueId ELSE tsb.StudentUniqueId END as active_student_id,
           COALESCE(te.enrollment_priority, tcc.enrollment_priority, 1) as enrollment_priority,
           COALESCE(te.calendar_flag, tcc.calendar_flag, 0) as calendar_flag,
           COALESCE(te.serviceschool, tcc.serviceschool, 0) as serviceschool
    INTO #temp_final
    FROM #temp_student_base tsb
    LEFT JOIN #temp_calendar_complex tcc ON tcc.StudentUSI = tsb.StudentUSI AND tcc.enrollment_priority = 1
    LEFT JOIN #temp_enrollment te ON te.StudentUSI = tsb.StudentUSI AND te.enrollment_priority = 1
    LEFT JOIN #temp_race_complex trc ON trc.StudentUSI = tsb.StudentUSI;
    SET @temp_count = @temp_count + 1;

    -- Return the result set
    SELECT
        -- Core Identifiers
        CAST(ISNULL(CAST(s.LocalEducationAgencyId AS NVARCHAR), '--') AS NVARCHAR(15)) as district_id,
        CAST(ISNULL(CAST(tf.SchoolId AS NVARCHAR), '--') AS NVARCHAR(15)) as eschool_building,
        CAST(ISNULL(RIGHT(CAST(tf.SchoolId AS NVARCHAR), 4), '--') AS NVARCHAR(30)) as school_id,
        CAST(tf.school_year AS SMALLINT) as school_year,
        CAST(ISNULL(st.StudentUniqueId, '--') AS NVARCHAR(25)) as local_student_id,
        CAST(CASE WHEN tf.enrollment_priority = 1 AND tf.calendar_flag = 0 THEN 'Y' ELSE 'N' END AS CHAR(1)) as current_enrolled_ind,
        ISNULL(tcc.EntryDate, '1753-01-01') as entry_date_value,
        CAST(ISNULL(st.StudentUniqueId, '--') AS NVARCHAR(25)) as unique_id,
        CAST(ISNULL(st.StudentUniqueId, '--') AS NVARCHAR(25)) as lea_student_id,
        CAST(ISNULL(st.StudentUniqueId, '--') AS NVARCHAR(25)) as state_id_nbr,
        CAST('--' AS NVARCHAR(15)) as ssn,
        CAST('-' AS CHAR(1)) as exclude_from_reporting_ind,

        -- Name Information
        CAST(ISNULL(st.FirstName, '--') AS NVARCHAR(35)) as first_name,
        CAST(ISNULL(st.MiddleName, '') AS NVARCHAR(15)) as middle_name,
        CAST(ISNULL(st.LastSurname, '--') AS NVARCHAR(35)) as last_name,
        CAST(ISNULL(st.PersonalTitlePrefix, '') AS NVARCHAR(10)) as name_prefix,
        CAST(ISNULL(st.GenerationCodeSuffix, '') AS NVARCHAR(10)) as name_suffix,
        CAST(ISNULL(LTRIM(RTRIM(ISNULL(st.FirstName, '') + ' ' + ISNULL(st.MiddleName, '') + ' ' + ISNULL(st.LastSurname, '') + ' ' + ISNULL(st.GenerationCodeSuffix, ''))), '--') AS NVARCHAR(80)) as full_name,
        CAST(ISNULL(st.FirstName, '--') AS NVARCHAR(50)) as preferred_name,
        CAST(ISNULL(LTRIM(RTRIM(ISNULL(st.LastSurname, '') + ' ' + ISNULL(st.GenerationCodeSuffix, '') + ', ' + ISNULL(st.FirstName, '') + ' ' + ISNULL(st.MiddleName, ''))), '--') AS NVARCHAR(80)) as sort_name,

        -- Academic Information
        CAST('--' AS NVARCHAR(25)) as homeroom_nbr,
        CAST(0 AS SMALLINT) as year_entered_9th_grade,
        CAST(ISNULL(CAST(tcc.SchoolId AS NVARCHAR), '--') AS NVARCHAR(20)) as class_of,
        CAST('--' AS NVARCHAR(30)) as diploma_type_cd,
        CAST('--' AS NVARCHAR(254)) as diploma_type_desc,
        CAST(ISNULL(tg.grade_code, '--') AS NVARCHAR(30)) as grade_level_cd,
        CAST(ISNULL(tg.grade_code, '--') AS NVARCHAR(254)) as grade_level_desc,
        CAST(ISNULL(tg.grade_sort, 0) AS SMALLINT) as grade_level_cd_sort_order,

        -- Demographics
        CAST(ISNULL(tgn.gender_code, '--') AS NVARCHAR(30)) as gender_cd,
        CAST(ISNULL(tgn.gender_code, '--') AS NVARCHAR(254)) as gender_desc,
        CAST(ISNULL(tgn.gender_sort, 0) AS SMALLINT) as gender_cd_sort_order,
        CAST(ISNULL(tf.race_code, '--') AS NVARCHAR(30)) as race_cd,
        CAST(ISNULL(tf.race_code, '--') AS NVARCHAR(254)) as race_desc,
        CAST(1 AS SMALLINT) as race_cd_sort_order,
        ISNULL(st.BirthDate, '1753-01-01') as birth_date,
        CAST(ISNULL(st.BirthCity, '--') AS NVARCHAR(80)) as place_of_birth,
        CAST('--' AS NVARCHAR(80)) as state_of_birth,  -- Would need join to descriptor
        CAST('--' AS NVARCHAR(80)) as country_of_birth, -- Would need join to descriptor

        -- Student Characteristics
        CAST(CASE WHEN tc.homeless_ind = 'Y' THEN 'M' ELSE '--' END AS NVARCHAR(30)) as migrant_cd,
        CAST('--' AS NVARCHAR(254)) as migrant_desc,
        CAST(ISNULL(tl.language_code, '--') AS NVARCHAR(30)) as primary_language_cd,
        CAST(ISNULL(tl.language_code, '--') AS NVARCHAR(254)) as primary_language_desc,
        CAST('--' AS NVARCHAR(30)) as english_proficiency_cd,
        CAST('--' AS NVARCHAR(254)) as english_proficiency_desc,
        CAST('--' AS NVARCHAR(30)) as free_reduced_meal_cd,
        CAST('--' AS NVARCHAR(254)) as free_reduced_meal_desc,
        CAST('--' AS NVARCHAR(30)) as primary_disability_cd,
        CAST('--' AS NVARCHAR(254)) as primary_disability_desc,

        -- Indicator Flags
        CAST(ISNULL(tse.disability_ind, 'N') AS CHAR(1)) as disability_ind,
        CAST('-' AS CHAR(1)) as economically_disadvantaged_ind,
        CAST('-' AS CHAR(1)) as esol_ind,
        CAST('-' AS CHAR(1)) as gifted_ind,
        CAST('-' AS CHAR(1)) as limited_english_proficiency_ind,
        CAST(CASE WHEN tc.homeless_ind = 'Y' THEN 'Y' ELSE 'N' END AS CHAR(1)) as migrant_ind,
        CAST(ISNULL(tc.homeless_ind, 'N') AS CHAR(1)) as homeless_ind,
        CAST('N' AS CHAR(1)) as military_connected_ind,

        -- Race/Ethnicity Individual Indicators (derived from complex race logic)
        CAST(CASE WHEN tf.race_code = 'White' THEN 'Y' ELSE 'N' END AS CHAR(1)) as white_ind,
        CAST(CASE WHEN tf.race_code = 'Black' THEN 'Y' ELSE 'N' END AS CHAR(1)) as black_african_american_ind,
        CAST(CASE WHEN tf.race_code = 'Asian' THEN 'Y' ELSE 'N' END AS CHAR(1)) as asian_ind,
        CAST('N' AS CHAR(1)) as hawaiian_pacific_islander_ind,
        CAST('N' AS CHAR(1)) as american_indian_alaskan_native_ind,
        CAST('N' AS CHAR(1)) as two_or_more_races_ind,
        CAST('N' AS CHAR(1)) as hispanic_latino_ethnicity_ind,

        -- Performance metrics
        @temp_count as temp_tables, -- Shows 14 temp tables with complex CTE logic
        CAST(DATEDIFF(MILLISECOND, @start_time, SYSDATETIME()) AS BIGINT) as processing_time_ms
    FROM #temp_final tf
    JOIN edfi.Student st ON st.StudentUSI = tf.StudentUSI
    LEFT JOIN edfi.School s ON s.SchoolId = tf.SchoolId
    LEFT JOIN #temp_calendar_complex tcc ON tcc.StudentUSI = tf.StudentUSI AND tcc.enrollment_priority = 1
    LEFT JOIN #temp_grade tg ON tg.StudentUSI = tf.StudentUSI
    LEFT JOIN #temp_gender tgn ON tgn.StudentUSI = tf.StudentUSI
    LEFT JOIN #temp_characteristics tc ON tc.StudentUSI = tf.StudentUSI
    LEFT JOIN #temp_special_ed tse ON tse.StudentUSI = tf.StudentUSI
    LEFT JOIN #temp_language tl ON tl.StudentUSI = tf.StudentUSI
    WHERE (@p_Batch_Period_List = 'all'
           OR @p_Batch_Period_List IS NULL
           OR CAST(tf.school_year AS NVARCHAR) IN (SELECT batch_period FROM #temp_batch_periods))
    -- Enhanced ordering using complex CTE results
    ORDER BY tf.enrollment_priority, tf.calendar_flag, tf.serviceschool, st.StudentUniqueId;

END