Feature: SuperclassReferenceValidation of Creation, Update and Deletion of resources

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                                     |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other local education agency |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                                    |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual                                     |
            And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution           | localEducationAgencyCategoryDescriptor                                                  | categories                                                                                                                             |
                  | 101                    | Local Education Agency Test | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other local education agency    | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"}]  |
            And the system has these "Schools"
                  | schoolId | nameOfInstitution | localEducationAgencyReference   | educationOrganizationCategories                                                                                         | gradeLevels                                                                          |
                  | 100      | School Test       | {"localEducationAgencyId":101}  | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"}]   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"}]      |


        Scenario: 01 Ensure clients can create a Program that references an existing School
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                      "programName": "Program School Test",
                      "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 100
                      }
                  }
                  """
             Then it should respond with 201 or 200


        Scenario: 02 Ensure clients can create a Program that references an existing Local Education Agency
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                      "programName": "Program LEA Test",
                      "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 101
                      }
                  }
                  """
             Then it should respond with 201 or 200


        Scenario: 03 Ensure clients cannot create a Program that references a non-existing Education Organization
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                      "programName": "Program Non-Existing Test",
                      "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 102
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced EducationOrganization item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        Scenario: 04 Ensure clients can update a school that references to an existing local education agency
            Given the system has these "Schools" references
                  | schoolId | nameOfInstitution | educationOrganizationCategories                                                                                         | gradeLevels                                                                          |
                  | 200      | School Test  99   | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"}]   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"}]      |
            And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution           | localEducationAgencyCategoryDescriptor                                                   | categories                                                                                                                             |
                  | 333                    | Other Education Agency Test | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other local education agency"    | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"}]  |
             When a PUT request is made to referenced resource "/ed-fi/schools/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolId": 200,
                      "nameOfInstitution": "School Test 99",
                      "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                        }
                      ],
                      "gradeLevels": [
                        {
                            "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                        }
                      ],
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 333
                      }
                  }
                  """
             Then it should respond with 204


        Scenario: 05 Ensure clients cannot update a school that references to an existing Local Agency so it references now a Non Existing Education Organization
            Given the system has these "schools" references
                  | schoolId | nameOfInstitution | localEducationAgencyReference   | educationOrganizationCategories                                                                                         | gradeLevels                                                                          |
                  | 222      | School Test  55   | {"localEducationAgencyId":101}  | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"}]   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"}]      |
              When a PUT request is made to referenced resource "/ed-fi/schools/{id}" with
                   """
                  {
                      "id": "{id}",
                      "schoolId": 222,
                      "nameOfInstitution": "School Test 55",
                      "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                        }
                      ],
                      "gradeLevels": [
                        {
                            "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                        }
                      ],
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 777
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced LocalEducationAgency item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """


        Scenario: 06 Ensure clients cannot delete and existing Education Organization that is referenced to a Program
            Given the system has these "localEducationAgencies" references
                  | localEducationAgencyId | nameOfInstitution           | localEducationAgencyCategoryDescriptor                                                  | categories                                                                                                                             |
                  | 333                    | Other Education Agency Test | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other local education agency    | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"}]  |
            And the system has these "programs"
                  | programName              | programTypeDescriptor                              | educationOrganizationReference     | programId |
                  | Career and Technical     | "uri://ed-fi.org/ProgramTypeDescriptor#Billingual" | {"educationOrganizationId":333}    | "111"     |
             When a DELETE request is made to referenced resource "/ed-fi/localEducationAgencies/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by existing Program, School item(s).",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

