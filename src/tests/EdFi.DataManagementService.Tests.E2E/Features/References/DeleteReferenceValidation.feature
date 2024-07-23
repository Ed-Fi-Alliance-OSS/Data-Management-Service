Feature: Validation of DELETE requests that would cause a foreign key violation

        Background:
            Given the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | false             | 2021-2022             |
              And the system has these descriptors
                  | descriptor                                                     |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/ProgramTypeDescriptor#CTE                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#First                     |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Ind     |
              And the system has these "calendars"
                  | calendarCode | schoolId  | schoolYear | calendarTypeDescriptor |
                  | 2010605675   | 255901107 | 2022       | ["Student Specific"]   |
              And the system has these "schools"
                  | educationOrganizationCategoryDescriptor | gradeLevels | schoolId  | nameOfInstitution            |
                  | ["School"]                              | ["First"]   | 255901107 | Grand Bend Elementary School |
              And the system has these "students"
                  | studentUniqueId | birthDate  | firstName | lastSurname |
                  | 604824          | 2010-01-13 | Traci     | Mathews     |
              And the system has these "localEducationAgencies"
                  | educationOrganizationCategoryDescriptor | localEducationAgencyId | localEducationAgencyCategoryDescriptor | nameOfInstitution |
                  | ["School"]                              | 255901                 | ["Ind"]                                | Grand Bend ISD    |
              And the system has these "programs"
                  | programName                    | programTypeDescriptor | educationOrganizationId |
                  | Career and Technical Education | ["CTE"]               | 255901                  |
              And the system has these "studentProgramAssociations"
                  | beginDate  | educationOrganizationId | programName                    | programTypeDescriptor | studentUniqueId |
                  | 2024-06-20 | 255901                  | Career and Technical Education | ["CTE"]               | 604824          |


        @ignore
        Scenario: 01 Ensure clients cannot delete a year that is used by another item
            # Reposting the item right before the WHEN statement is the simplest way
            # of making the {id} available for the URL
            Given a POST request is made to "/ed-fi/schoolYearTypes" with
                  """
                  {
                   "schoolYear": 2022,
                   "currentSchoolYear": false,
                   "schoolYearDescription": "2021-2022"
                  }
                  """
             When a DELETE request is made to "/ed-fi/schoolYearTypes/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by an existing 'Calendar' item.",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 02 Ensure clients cannot delete a descriptor that is used by another item
            Given a POST request is made to "/ed-fi/educationOrganizationCategoryDescriptors" with
                  """
                  {
                      "codeValue": "School",
                      "shortDescription": "School",
                      "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor"
                  }
                  """
             When a DELETE request is made to "/ed-fi/educationOrganizationCategoryDescriptors/{id}"
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

        @ignore
        Scenario: 03 Ensure clients cannot delete a dependent element for an item
            Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604824",
                      "birthDate": "2010-01-13",
                      "firstName": "Traci",
                      "lastSurname": "Mathews"
                  }
                  """
             When a DELETE request is made to "/ed-fi/students/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by an existing 'StudentProgramAssociation' item.",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 04 Ensure clients cannot delete an element that is reference to an Education Organization that is used by another items
            Given a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "educationOrganizationCategoryDescriptor": ["uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"],
                      "localEducationAgencyId": 255901,
                      "localEducationAgencyCategoryDescriptor": ["uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Ind"],
                      "nameOfInstitution": "Grand Bend ISD"
                  }
                  """
             When a DELETE request is made to "/ed-fi/localEducationAgencies/{id}"
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

        @ignore
        Scenario: 05 Ensure clients cannot delete a resource that is used by another items
            Given a POST request is made to "/ed-fi/programs" with
                  """
                  {
                      "programName": "Career and Technical Education"
                      "programTypeDescriptor": ["uri://ed-fi.org/ProgramTypeDescriptor#CTE"],
                      "educationOrganizationId": 255901
                  }
                  """
             When a DELETE request is made to "/ed-fi/programs/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The requested action cannot be performed because this item is referenced by an existing 'StudentProgramAssociation' item.",
                      "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                      "title": "Dependent Item Exists",
                      "status": 409,
                      "correlationId": null
                  }
                  """
