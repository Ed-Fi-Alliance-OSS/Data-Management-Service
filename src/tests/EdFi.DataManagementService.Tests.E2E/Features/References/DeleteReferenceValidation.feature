Feature: Delete reference validation
    DELETE requests validation for invalid references

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school |
                  | uri://ed-fi.org/CalendarTypeDescriptor#Student Specific        |
              And the system has these "schools"
                  | educationOrganizationCategories                                                                                   | gradeLevels                                                                      | schoolId  | nameOfInstitution            |
                  | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First grade"} ] | 255901107 | Grand Bend Elementary School |

        @API-084
        Scenario: 01 Ensure clients cannot delete a year that is used by another item
            Given the system has these "schoolYearTypes" references
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | false             | 2021-2022             |
              And the system has these "calendars"
                  | calendarCode | schoolReference        | schoolYearTypeReference | calendarTypeDescriptor                                  |
                  | "2010605675" | {"schoolId":255901107} | {"schoolYear":2022}     | uri://ed-fi.org/CalendarTypeDescriptor#Student Specific |
             When a DELETE request is made to referenced resource "/ed-fi/schoolYearTypes/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by existing Calendar item(s).",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @API-085
        Scenario: 02 Ensure clients cannot delete a descriptor that is used by another item
            Given a POST request is made to "ed-fi/educationOrganizationCategoryDescriptors/" with
                  """
                  {
                      "codeValue": "school",
                      "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
                      "shortDescription": "school"
                  }
                  """
             When a DELETE request is made to "ed-fi/educationOrganizationCategoryDescriptors/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by existing School item(s).",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @API-086
        Scenario: 03 Ensure clients cannot delete a dependent element for an item
            Given the system has these "students" references
                  | studentUniqueId | birthDate    | firstName | lastSurname |
                  | "604824"        | "2010-01-13" | Traci     | Mathews     |
              And the system has these "studentEducationOrganizationAssociations"
                  | educationOrganizationReference        | studentReference             |
                  | {"educationOrganizationId":255901107} | {"studentUniqueId":"604824"} |
             When a DELETE request is made to referenced resource "/ed-fi/students/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by existing StudentEducationOrganizationAssociation item(s).",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @API-087
        Scenario: 04 Ensure clients cannot delete an element that is reference to an Education Organization that is used by another items
            Given the system has these "localEducationAgencies" references
                  | localEducationAgencyCategoryDescriptor                        | localEducationAgencyId | categories                                                                                                                  | nameOfInstitution |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#School | 333                    | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Independent" }] | Grand Bend Delete |
              And the system has these "schools"
                  | educationOrganizationCategories                                                                                   | gradeLevels                                                                      | schoolId  | nameOfInstitution            | localEducationAgencyReference   |
                  | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First grade"} ] | 255901108 | Grand Bend Elementary School | {"localEducationAgencyId": 333} |
             When a DELETE request is made to referenced resource "ed-fi/localEducationAgencies/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by existing School item(s).",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @API-088
        Scenario: 05 Ensure clients cannot delete a resource that is used by another items
            Given the system has these "programs" references
                  | programName                    | programTypeDescriptor                                                | educationOrganizationReference        |
                  | Career and Technical Education | uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education | {"educationOrganizationId":255901107} |
              And the system has these "students"
                  | studentUniqueId | birthDate    | firstName | lastSurname |
                  | "604844"        | "2010-01-13" | Traci     | Mathews     |
              And the system has these "studentProgramAssociations"
                  | beginDate    | educationOrganizationReference        | programReference                                                                                                                                                                           | studentReference              |
                  | "2024-06-20" | {"educationOrganizationId":255901107} | {"educationOrganizationId":255901107,  "programName": "Career and Technical Education", "programTypeDescriptor" : "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education" } | {"studentUniqueId": "604844"} |
             When a DELETE request is made to referenced resource "ed-fi/programs/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by existing StudentProgramAssociation item(s).",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
