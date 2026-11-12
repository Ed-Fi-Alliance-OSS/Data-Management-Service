Feature: Identity Conflict validation

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
            And the system has these descriptors
                  | descriptorValue                                             |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary         |
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
        @e2e-ci-shard-1
        @MssqlRepresentative
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
                      "type": "urn:ed-fi:api:data-conflict:non-unique-identity",
                      "title": "Identifying Values Are Not Unique",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                      "A natural key conflict occurred when attempting to create a new resource School with a duplicate key. The duplicate keys and values are (schoolId = 155901)"
                      ]
                  }
                  """

        @e2e-ci-shard-1
        @MssqlRepresentative
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
                      "type": "urn:ed-fi:api:data-conflict:non-unique-identity",
                      "title": "Identifying Values Are Not Unique",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                        "A natural key conflict occurred when attempting to update a resource ClassPeriod with a duplicate key. The duplicate keys and values are (classPeriodName = First),(schoolReference.schoolId = 255901001)"
                      ]
                  }
                  """

        # StudentProgramAssociation and StudentCTEProgramAssociation share the
        # GeneralStudentProgramAssociation identity, so the second POST is a new document that collides
        # on that shared identity. The association's education organization (School 255901001) and the
        # program's (LEA 155901) differ, so each educationOrganizationId must be named by its reference.
        @e2e-ci-shard-1
        @MssqlRepresentative
        Scenario: 03 Ensure duplicate identity values with the same property name are named by their reference
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                           |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented |
              And the system has these "programs"
                  | programName         | programTypeDescriptor                                     | educationOrganizationReference    |
                  | Gifted and Talented | uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented | {"educationOrganizationId": 155901} |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604890"        | Identity  | Conflict    | 2010-01-01 |
              And the system has these "studentProgramAssociations"
                  | beginDate  | educationOrganizationReference         | programReference                                                                                                                                  | studentReference               |
                  | 2025-08-01 | {"educationOrganizationId": 255901001} | {"educationOrganizationId": 155901, "programName": "Gifted and Talented", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented"} | {"studentUniqueId": "604890"} |
             When a POST request is made to "/ed-fi/studentCTEProgramAssociations" with
                  """
                  {
                      "beginDate": "2025-08-01",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901001
                      },
                      "programReference": {
                          "educationOrganizationId": 155901,
                          "programName": "Gifted and Talented",
                          "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented"
                      },
                      "studentReference": {
                          "studentUniqueId": "604890"
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                      "type": "urn:ed-fi:api:data-conflict:non-unique-identity",
                      "title": "Identifying Values Are Not Unique",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                        "A natural key conflict occurred when attempting to create a new resource StudentCTEProgramAssociation with a duplicate key. The duplicate keys and values are (beginDate = 2025-08-01),(educationOrganizationReference.educationOrganizationId = 255901001),(programReference.educationOrganizationId = 155901),(programReference.programName = Gifted and Talented),(programReference.programTypeDescriptor = uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented),(studentReference.studentUniqueId = 604890)"
                      ]
                  }
                  """
