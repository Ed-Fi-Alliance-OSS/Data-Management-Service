Feature: OrganizationDepartment Authorization

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                     |
                  | 255901                 | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC |
              And the system has these "organizationDepartments"
                  | _storeResultingIdInVariable | organizationDepartmentId | nameOfInstitution | parentEducationOrganizationReference | categories                                                                                                                         |
                  | orgDepId                    | 255901101                | Test Office       | {"educationOrganizationId": 255901}  | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department" }] |

    Rule: When the client is authorized
        Scenario: 01 Ensure authorized client can create a OrganizationDepartment
             When a POST request is made to "/ed-fi/organizationDepartments" with
                  """
                  {
                    "parentEducationOrganizationReference": {
                        "educationOrganizationId": 255901
                    },
                    "organizationDepartmentId": 255901102,
                    "nameOfInstitution": "New Test Office",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department"
                        }
                    ]
                  }
                  """
             Then it should respond with 201

        Scenario: 02.1 Ensure authorized client can get a OrganizationDepartment by id
             When a GET request is made to "/ed-fi/organizationDepartments/{orgDepId}"
             Then it should respond with 200

        Scenario: 02.2 Ensure authorized client can get a OrganizationDepartment by query
            Given a POST request is made to "/ed-fi/organizationDepartments" with
                  """
                  {
                    "parentEducationOrganizationReference": {
                        "educationOrganizationId": 255901
                    },
                    "organizationDepartmentId": 255901102,
                    "nameOfInstitution": "New Test Office",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department"
                        }
                    ]
                  }
                  """
             When a GET request is made to "/ed-fi/organizationDepartments?nameOfInstitution=New Test Office"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "id": "{id}",
                        "parentEducationOrganizationReference": {
                            "educationOrganizationId": 255901
                        },
                        "organizationDepartmentId": 255901102,
                        "nameOfInstitution": "New Test Office",
                        "categories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department"
                            }
                        ]
                    }
                  ]
                  """

        Scenario: 03 Ensure authorized client can update a OrganizationDepartment
             When a PUT request is made to "/ed-fi/organizationDepartments/{orgDepId}" with
                  """
                  {
                    "id": "{orgDepId}",
                    "parentEducationOrganizationReference": {
                        "educationOrganizationId": 255901
                    },
                    "organizationDepartmentId": 255901101,
                    "nameOfInstitution": "Test Office",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department"
                        }
                    ]
                  }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure authorized client can delete a OrganizationDepartment
             When a DELETE request is made to "/ed-fi/organizationDepartments/{orgDepId}"
             Then it should respond with 204

    Rule: When the client is unauthorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"

        Scenario: 05 Ensure unauthorized client can not create a OrganizationDepartment
             When a POST request is made to "/ed-fi/organizationDepartments" with
                  """
                  {
                    "parentEducationOrganizationReference": {
                        "educationOrganizationId": 255901
                    },
                    "organizationDepartmentId": 255901102,
                    "nameOfInstitution": "New Test Office",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department"
                        }
                    ]
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """

        Scenario: 06.1 Ensure unauthorized client can not get a OrganizationDepartment by id
             When a GET request is made to "/ed-fi/organizationDepartments/{orgDepId}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """

        Scenario: 06.2 Ensure unauthorized client can not get a OrganizationDepartment by query
             When a GET request is made to "/ed-fi/organizationDepartments?nameOfInstitution=Test Office"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        Scenario: 07 Ensure unauthorized client can not update a OrganizationDepartment
             When a PUT request is made to "/ed-fi/organizationDepartments/{orgDepId}" with
                  """
                  {
                    "id": "{orgDepId}",
                    "parentEducationOrganizationReference": {
                        "educationOrganizationId": 255901
                    },
                    "organizationDepartmentId": 255901102,
                    "nameOfInstitution": "New Test Office",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department"
                        }
                    ]
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """

        Scenario: 08 Ensure unauthorized client can not delete a OrganizationDepartment
             When a DELETE request is made to "/ed-fi/organizationDepartments/{orgDepId}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """
