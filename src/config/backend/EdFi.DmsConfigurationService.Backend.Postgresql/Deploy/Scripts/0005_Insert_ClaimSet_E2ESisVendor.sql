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
		'stateAbbreviationDescriptors'
    ] AS names
)
INSERT INTO resource_claims (name)
SELECT unnest(names) FROM resource_names;

-- Step 3: Insert the generated JSON into claimset table
INSERT INTO dmscs.claimset (claimsetname, issystemreserved, resourceclaims)
SELECT
    'E2E-SIS-Vendor',
	true,
    jsonb_agg(
        jsonb_build_object(
            'id', r.id,
            'name', r.name,
            'actions', jsonb_build_array(
                jsonb_build_object('name', 'Create', 'enabled', true),
                jsonb_build_object('name', 'Delete', 'enabled', true),
                jsonb_build_object('name', 'Update', 'enabled', true),
                jsonb_build_object('name', 'Read', 'enabled', true)
            ),
            '_defaultAuthorizationStrategiesForCRUD', jsonb_build_array(
                jsonb_build_object(
                    'actionId', 1,
                    'actionName', 'Create',
                    'authorizationStrategies', jsonb_build_array(
                        jsonb_build_object(
                            'authStrategyId', 1,
                            'authStrategyName', 'NoFurtherAuthorizationRequired',
                            'isInheritedFromParent', false
                        )
                    )
                ),
                jsonb_build_object(
                    'actionId', 2,
                    'actionName', 'Read',
                    'authorizationStrategies', jsonb_build_array(
                        jsonb_build_object(
                            'authStrategyId', 2,
                            'authStrategyName', 'RelationshipsWithEdOrgsAndPeople',
                            'isInheritedFromParent', false
                        )
                    )
                ),
                jsonb_build_object(
                    'actionId', 3,
                    'actionName', 'Update',
                    'authorizationStrategies', jsonb_build_array(
                        jsonb_build_object(
                            'authStrategyId', 2,
                            'authStrategyName', 'RelationshipsWithEdOrgsAndPeople',
                            'isInheritedFromParent', false
                        )
                    )
                ),
                jsonb_build_object(
                    'actionId', 4,
                    'actionName', 'Delete',
                    'authorizationStrategies', jsonb_build_array(
                        jsonb_build_object(
                            'authStrategyId', 1,
                            'authStrategyName', 'NoFurtherAuthorizationRequired',
                            'isInheritedFromParent', false
                        )
                    )
                ),
                jsonb_build_object(
                    'actionId', 5,
                    'actionName', 'ReadChanges',
                    'authorizationStrategies', jsonb_build_array(
                        jsonb_build_object(
                            'authStrategyId', 9,
                            'authStrategyName', 'RelationshipsWithEdOrgsAndPeopleIncludingDeletes',
                            'isInheritedFromParent', false
                        )
                    )
                )
            ),
            'authorizationStrategyOverridesForCRUD', jsonb_build_array(),
            'children',  jsonb_build_array()
        )
    ) AS resourceClaims
FROM resource_claims r;
