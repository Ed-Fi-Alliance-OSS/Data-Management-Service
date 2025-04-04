-- This script automatically generates the claimset needed to run the E2E tests using authorization

-- Step 1: Create a temporary table to store the resource names
DROP TABLE IF EXISTS resource_claims;
CREATE TEMPORARY TABLE resource_claims (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL
);

-- Step 2: Insert the resource names
WITH resource_names AS (
    SELECT ARRAY[
	--resources:
		'academicWeeks',
		'assessments',
		'bellschedules',
		'calendars',
		'calendarDates',
		'classPeriods',
		'contacts',
		'courses',
		'courseOfferings',
		'educationContents',
		'educationOrganizations',
		'gradingPeriods',
		'grades',
		'generalStudentProgramAssociations',
		'graduationPlans',
		'localEducationAgencies',
		'programs',
		'reportCards',
		'schools',
		'schoolYearTypes',
		'sections',
		'sessions',
		'staffs',
		'students',
		'studentAssessments',
		'studentContactAssociations',
		'studentCTEProgramAssociations',
		'studentEducationOrganizationAssociations',
		'studentProgramAssociations',
		'studentSchoolAssociations',
		'studentSectionAssociations',
        'surveys',
        'credentials',
	--descriptors:
		'absenceEventCategoryDescriptors',
--		'academicHonorCategoryDescriptors',
		'academicSubjectDescriptors',
		'addressTypeDescriptors',
		'assessmentCategoryDescriptors',
		'assessmentReportingMethodDescriptors',
		'calendarEventDescriptors',
		'calendarTypeDescriptors',
		'contentClassDescriptors',
		'courseIdentificationSystemDescriptors',
		'disabilityDescriptors',
		'educationOrganizationCategoryDescriptors',
		'gradeLevelDescriptors',
		'gradeTypeDescriptors',
		'gradingPeriodDescriptors',
		'graduationPlanTypeDescriptors',
		'localEducationAgencyCategoryDescriptors',
		'performanceLevelDescriptors',
		'programTypeDescriptors',
		'resultDatatypeTypeDescriptors',
		'termDescriptors',
		'stateAbbreviationDescriptors',
        'credentialTypeDescriptors'
    ] AS names
)
INSERT INTO resource_claims (name)
SELECT unnest(names) FROM resource_names;

-- Step 3: Insert the generated JSON into claimset table
INSERT INTO dmscs.claimset (claimsetname, issystemreserved)
SELECT
    'E2E-NoFurtherAuthRequiredClaimSet',
	true
FROM resource_claims r;
