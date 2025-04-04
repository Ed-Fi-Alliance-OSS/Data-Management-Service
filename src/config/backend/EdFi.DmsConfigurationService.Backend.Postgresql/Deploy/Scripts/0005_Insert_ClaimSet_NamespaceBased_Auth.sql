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
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('schoolYearTypes', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('surveys', 'NamespaceBased', 'NamespaceBased', 'NamespaceBased', 'NamespaceBased');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('absenceEventCategoryDescriptors', 'NamespaceBased', 'NamespaceBased', 'NamespaceBased', 'NamespaceBased');

-- Step 3: Insert the generated JSON into claimset table
INSERT INTO dmscs.claimset (claimsetname, issystemreserved)
SELECT
    'E2E-NameSpaceBasedClaimSet',
	true
FROM resource_claims r;
