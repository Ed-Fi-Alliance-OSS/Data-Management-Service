INSERT INTO dmscs.ClaimSet (
    claimsetname,
    issystemreserved,
    resourceclaims
) VALUES (
    'SIS-Vendor',
    true,
   '[
      {
        "id": 1,
        "name": "absenceEventCategoryDescriptors",
        "actions": [
          {
            "name": "Create",
            "enabled": true
          },
          {
            "name": "Delete",
            "enabled": true
          },
          {
            "name": "Update",
            "enabled": true
          },
          {
            "name": "Read",
            "enabled": true
          }
        ],
        "children": [
        ],
        "authorizationStrategyOverridesForCRUD": [
        ],
        "defaultAuthorizationStrategiesForCRUD": [
          {
            "actionId": 1,
            "actionName": "Create",
            "authorizationStrategies": [
              {
                "authStrategyId": 1,
                "authStrategyName": "NoFurtherAuthorizationRequired",
                "isInheritedFromParent": false
              }
            ]
          }
        ]
      }
    ]'
);
