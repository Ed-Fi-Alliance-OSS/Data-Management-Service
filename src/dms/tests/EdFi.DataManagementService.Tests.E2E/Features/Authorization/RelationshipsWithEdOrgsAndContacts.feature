Feature: RelationshipsWithEdOrgsAndContacts Authorization

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "255901001, 244901"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2023       | true              | "year 2023"           |
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/CourseAttemptResultDescriptor#Pass                       |
                  | uri://ed-fi.org/TermDescriptor#Semester                                  |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code    |
                  | uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew              |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |

    Rule: StudentSchoolAssociation CRUD is properly authorized
        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901901 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "S91111"         | Authorized student   | student-ln  | 2008-01-01 |
              And the system has these "contacts"
                  | contactUniqueId | firstName            | lastSurname |
                  | "C91111"         | Authorized contact   | contact-ln  |
        Scenario: 01 Ensure client can create a StudentContactAssociation
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "S91111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C91111"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91111"
                      },
                      "emergencyContactStatus": true
                  }
                  """
             Then it should respond with 201
