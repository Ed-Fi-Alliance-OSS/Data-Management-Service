Feature: SuperclassReferenceValidation of Creation, Update and Deletion of resources

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                                     |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other local education agency |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                                    |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual                                     |
            Given the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution           | localEducationAgencyCategoryDescriptor                                                  | categories                                                                                                                             |
                  | 101                    | Local Education Agency Test | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other local education agency    | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"}]  |
              And the system has these "Schools"
                  | schoolId | nameOfInstitution | localEducationAgencyReference   | educationOrganizationCategories                                                                                         | gradeLevels                                                                          |
                  | 100      | School Test       | {"localEducationAgencyId":101}  | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"}]   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"}]      |


        Scenario: 01 Ensure clients can create a Program that references an existing School
             When a POST request is made to "ed-fi/programs" with
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
             When a POST request is made to "ed-fi/programs" with
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
             When a POST request is made to "ed-fi/programs" with
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
                      "correlationId": null
                  }
                  """
        @ignore
        Scenario: 04 Ensure clients can update a program that references to an existing school so it references now the Local Education Agency
            Given programName Program School Test
            #set value to {id}
             When a PUT request is made to "ed-fi/programs/{id}" with
                  """
                  {
                      "id":{id}
                      "programName": "Program School Test",
                      "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 101
                      }
                  }
                  """
             Then it should respond with 204

        @ignore
        Scenario: 05 Ensure clients cannot update a program that references to an existing Local Agency so it references now a Non Existing Education Organization
            Given programName Program LEA Test
        #set value to {id}
             When a PUT request is made to "ed-fi/programs/{id}" with
                  """
                  {
                      "programName": "Program LEA Test",
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
                      "detail": "The referenced 'EducationOrganization' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 06 Ensure clients cannot delete and existing Education Organization that is referenced to a Program
            Given localEducationAgencyId 101
        #set value to {id}
             When a DELETE request is made to "ed-fi/localEducationAgencies/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by an existing 'School' item.",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null
                  }
                  """

