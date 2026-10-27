@reset-data-before-scenario
Feature: API client education organization scope authorization
    An SEA keeps one Application per vendor product and issues each LEA its own API client, narrowed
    to that LEA's education organizations. The narrowed client's real token carries only its own
    list, and DMS authorizes it by that list: its own school's data is readable and another school's
    is denied. The bell schedules use RelationshipsWithEdOrgsOnly authorization by schoolId.

        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 255901002"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | LEA One school    | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901002 | LEA Two school    | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 01 - Traditional | { "schoolId": 255901002 } |
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": { "schoolId": 255901001 },
                      "bellScheduleName": "LEA One Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [ { "classPeriodReference": { "classPeriodName": "01 - Traditional", "schoolId": 255901001 } } ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "leaOneSchedule" variable
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": { "schoolId": 255901002 },
                      "bellScheduleName": "LEA Two Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [ { "classPeriodReference": { "classPeriodName": "01 - Traditional", "schoolId": 255901002 } } ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "leaTwoSchedule" variable
             # Both resources exist and the Application's own client can read them
             When a GET request is made to "/ed-fi/bellschedules/{leaOneSchedule}"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/bellschedules/{leaTwoSchedule}"
             Then it should respond with 200

        @e2e-ci-shard-4 @MssqlRepresentative
        Scenario: 01 Each LEA's narrowed client reads only its own school's data
            Given API client "leaOne" narrowed to educationOrganizationIds "255901001" is created on the Application
              And API client "leaTwo" narrowed to educationOrganizationIds "255901002" is created on the Application
             When the token of API client "leaOne" is used
             Then the current token carries exactly the education organizations "255901001"
             When a GET request is made to "/ed-fi/bellschedules/{leaOneSchedule}"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/bellschedules/{leaTwoSchedule}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claim ('255901001') and the resource item's 'SchoolId' value."
                      ]
                  }
                  """
              And the response body has a non-empty correlationId
             When the token of API client "leaTwo" is used
             Then the current token carries exactly the education organizations "255901002"
             When a GET request is made to "/ed-fi/bellschedules/{leaTwoSchedule}"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/bellschedules/{leaOneSchedule}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claim ('255901002') and the resource item's 'SchoolId' value."
                      ]
                  }
                  """
              And the response body has a non-empty correlationId

        # The Application update refreshes the Application's first client. Removing the only education
        # organization of a narrowed first client leaves its token with no education organization access.
        @e2e-ci-shard-4 @MssqlRepresentative
        Scenario: 02 A narrowed client whose only education organization leaves its Application has no access
            Given an Application without default credentials for claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" with educationOrganizationIds "255901001, 255901002"
              And API client "emptied" narrowed to educationOrganizationIds "255901002" is created on the Application
             When the token of API client "emptied" is used
             Then the current token carries exactly the education organizations "255901002"
             When a GET request is made to "/ed-fi/bellschedules/{leaTwoSchedule}"
             Then it should respond with 200
             When the Application's educationOrganizationIds are changed to "255901001"
              And the token of API client "emptied" is used
             Then the current token carries no education organizations
             When a GET request is made to "/ed-fi/bellschedules/{leaTwoSchedule}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims (none) and the resource item's 'SchoolId' value."
                      ]
                  }
                  """
              And the response body has a non-empty correlationId
             When a GET request is made to "/ed-fi/bellschedules/{leaOneSchedule}"
             Then it should respond with 403
