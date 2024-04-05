Feature: Equality Constraint Validation
    Equality constraints on the resource describe values that must be equal when posting a resource. An example of an equalityConstraint on bellSchedule:
    "equalityConstraints": [
        {
            "sourceJsonPath": "$.classPeriods[*].classPeriodReference.schoolId",
            "targetJsonPath": "$.schoolReference.schoolId"
        }
    ]

Scenario: Post a valid bell schedule no equality constraint violations.
    When sending a POST request to "ed-fi/bellschedules" with body
            """
            {
                "schoolReference": {
                    "schoolId": 255901001
                },
                "bellScheduleName": "Test Schedule",
                "totalInstructionalTime": 325,
                "classPeriods": [
                    {
                    "classPeriodReference": {
                        "classPeriodName": "01 - Traditional",
                        "schoolId": 255901001
                    }
                    },
                    {
                    "classPeriodReference": {
                        "classPeriodName": "02 - Traditional",
                        "schoolId": 255901001
                    }
                    }
                ],
                "dates": [],
                "gradeLevels": []
                }
            """
    Then the response code is 201

Scenario: Post an invalid bell schedule with equality constraint violations.
    When sending a POST request to "ed-fi/bellschedules" with body
            """
            {
                "schoolReference": {
                    "schoolId": 255901001
                },
                "bellScheduleName": "Test Schedule",
                "totalInstructionalTime": 325,
                "classPeriods": [
                    {
                    "classPeriodReference": {
                        "classPeriodName": "01 - Traditional",
                        "schoolId": 1
                    }
                    },
                    {
                    "classPeriodReference": {
                        "classPeriodName": "02 - Traditional",
                        "schoolId": 1
                    }
                    }
                ],
                "dates": [],
                "gradeLevels": []
                }
            """
    Then the response code is 400
        And the response body is
"""
{"detail":"Data validation failed. See errors for details.","type":"urn:dms:bad-request:data","title":"Data Validation Error","status":400,"correlationId":null,"validationErrors":null,"errors":["Constraint failure: document paths $.classPeriods[*].classPeriodReference.schoolId and $.schoolReference.schoolId must have the same values"]}
"""
