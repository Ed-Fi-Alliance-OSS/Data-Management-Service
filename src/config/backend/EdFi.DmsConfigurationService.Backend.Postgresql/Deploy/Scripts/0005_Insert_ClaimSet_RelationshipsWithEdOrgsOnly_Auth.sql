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
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('classPeriods', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('academicWeeks', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');
INSERT INTO resource_claims (name, create_auth_strategy, update_auth_strategy, read_auth_strategy, delete_auth_strategy) VALUES('bellSchedules', 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnly', 'NoFurtherAuthorizationRequired', 'NoFurtherAuthorizationRequired');

-- Step 3: Insert the generated JSON into claimset table
INSERT INTO dmscs.claimset (claimsetname, issystemreserved, resourceclaims)
SELECT
    'E2E-RelationshipsWithEdOrgsOnlyClaimSet',
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
            'authorizationStrategyOverridesForCRUD', jsonb_build_array(
                jsonb_build_object(
                    'actionId', 1,
                    'actionName', 'Create',
                    'authorizationStrategies', jsonb_build_array(
                        jsonb_build_object(
                            'authStrategyId', 1,
                            'authStrategyName', r.create_auth_strategy,
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
                            'authStrategyName', r.read_auth_strategy,
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
                            'authStrategyName', r.update_auth_strategy,
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
                            'authStrategyName', r.delete_auth_strategy,
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
                            'authStrategyName', r.read_auth_strategy,
                            'isInheritedFromParent', false
                        )
                    )
                )
            ),
            '_defaultAuthorizationStrategiesForCRUD', jsonb_build_array(),
            'children',  jsonb_build_array()
        )
    ) AS resourceClaims
FROM resource_claims r;
