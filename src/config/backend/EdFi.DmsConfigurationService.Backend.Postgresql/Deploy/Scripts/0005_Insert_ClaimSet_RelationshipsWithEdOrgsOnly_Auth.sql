-- Step 1: Create a temporary table to store the resource names
DROP TABLE IF EXISTS resource_claims;
CREATE TEMPORARY TABLE resource_claims (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL,
	create_auth_strategy TEXT NOT NULL,
	update_auth_strategy TEXT NOT NULL,
  read_auth_strategy TEXT NOT NULL,
  delete_auth_strategy TEXT NOT NULL
);
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('schools', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('gradeLevelDescriptors', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('educationOrganizationCategoryDescriptors', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('localEducationAgencyCategoryDescriptors', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('stateEducationAgencies', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('localEducationAgencies', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('classPeriods', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('academicWeeks', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('bellSchedules', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly');

-- Step 3: Insert the generated JSON into claimset table
INSERT INTO dmscs.claimset (claimsetname, issystemreserved)
SELECT
    'E2E-RelationshipsWithEdOrgsOnlyClaimSet',
	true
FROM resource_claims r;
