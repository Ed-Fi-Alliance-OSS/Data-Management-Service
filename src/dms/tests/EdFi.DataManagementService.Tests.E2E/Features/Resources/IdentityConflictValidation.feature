Feature: Identity Conflict validation

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
            Given the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | localEducationAgencyCategoryDescriptor                             | categories                                                                                                                               |
                  | 155901                 | Grand Bend ISD    | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider" }] |
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
            Given the system has these "classPeriods"
                  | _storeResultingIdInVariable | schoolReference           | classPeriodName |
                  |                             | { "schoolId": 255901001 } | First           |
                  | secondClassPeriod           | { "schoolId": 255901001 } | Second          |

        @API-183
        Scenario: 01 Ensure client can't create a School with the same identity as another Education Organization
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 155901,
                      "nameOfInstitution": "School Test",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                      "type": "urn:ed-fi:api:identity-conflict",
                      "title": "Identifying Values Are Not Unique",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                      "A natural key conflict occurred when attempting to create a new resource School with a duplicate key. The duplicate keys and values are (schoolId = 155901)"
                      ]
                  }
                  """

        Scenario: 02 Ensure client can't create a resource with the same identity as another resource
             When a PUT request is made to "/ed-fi/classPeriods/{secondClassPeriod}" with
                  """
                  {
                      "id":"{secondClassPeriod}",
                        "schoolReference": {
                            "schoolId": 255901001
                        },
                        "classPeriodName": "First"
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                      "type": "urn:ed-fi:api:identity-conflict",
                      "title": "Identifying Values Are Not Unique",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                        "A natural key conflict occurred when attempting to update a resource ClassPeriod with a duplicate key. The duplicate keys and values are (classPeriodName = First),(schoolId = 255901001)"
                      ]
                  }
                  """
