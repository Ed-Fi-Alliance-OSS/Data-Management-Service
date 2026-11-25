@reset-data-before-scenario
Feature: Query resources by identification-code collection values
    A GET-many request may filter on the properties of a resource's identification-code collection.
    Every supplied identification-code filter must hold for the same collection element, a resource
    matched by several elements is returned once, and the existing root query fields of the same
    name keep their meaning.

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                           |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School            |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                          |
                  | uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#NCES  |
                  | uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#SEA   |
                  | uri://ed-fi.org/StaffIdentificationSystemDescriptor#State                 |
                  | uri://ed-fi.org/StaffIdentificationSystemDescriptor#District              |
                  | uri://ed-fi.org/TermDescriptor#Fall Semester                              |
                  | uri://ed-fi.org/CourseAttemptResultDescriptor#Pass                        |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code      |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code    |
              And the system has these "schools"
                  | schoolId | nameOfInstitution  | gradeLevels                                                                      | educationOrganizationCategories                                                                            | identificationCodes                                                                                                                                                                                                                                                                                                                        |
                  | 8101     | IdCodeSchoolAlpha  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | [ {"identificationCode": "C-100", "educationOrganizationIdentificationSystemDescriptor": "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#NCES"} ]                                                                                                                                                             |
                  | 8102     | IdCodeSchoolBravo  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | [ {"identificationCode": "C-200", "educationOrganizationIdentificationSystemDescriptor": "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#NCES"}, {"identificationCode": "C-200", "educationOrganizationIdentificationSystemDescriptor": "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#SEA"} ] |
                  | 8103     | IdCodeSchoolEcho   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | [ {"identificationCode": "C-100", "educationOrganizationIdentificationSystemDescriptor": "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#SEA"} ]                                                                                                                                                              |
                  | 8104     | IdCodeSchoolFoxtrot | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | [ {"identificationCode": "C-600", "educationOrganizationIdentificationSystemDescriptor": "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#NCES"}, {"identificationCode": "C-601", "educationOrganizationIdentificationSystemDescriptor": "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#SEA"} ] |
              And the system has these "schools"
                  | schoolId | nameOfInstitution  | gradeLevels                                                                      | educationOrganizationCategories                                                                            |
                  | 8105     | IdCodeSchoolCharlie | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "staffs"
                  | staffUniqueId | firstName        | lastSurname | identificationCodes                                                                                                                                                                                                                                                                                                                  |
                  | "IdCodeStaffA" | IdCodeStaffAlpha | Staff       | [ {"identificationCode": "X", "staffIdentificationSystemDescriptor": "uri://ed-fi.org/StaffIdentificationSystemDescriptor#State", "assigningOrganizationIdentificationCode": "O1"} ]                                                                                                                                                  |
                  | "IdCodeStaffB" | IdCodeStaffBravo | Staff       | [ {"identificationCode": "X", "staffIdentificationSystemDescriptor": "uri://ed-fi.org/StaffIdentificationSystemDescriptor#State", "assigningOrganizationIdentificationCode": "O2"}, {"identificationCode": "Y", "staffIdentificationSystemDescriptor": "uri://ed-fi.org/StaffIdentificationSystemDescriptor#District", "assigningOrganizationIdentificationCode": "O1"} ] |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 01 A filter selects only resources with a matching collection element
             When a GET request is made to "/ed-fi/schools?identificationCode=C-600&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body should contain "IdCodeSchoolFoxtrot"
              And the response headers include
                  """
                    {
                        "total-count": 1
                    }
                  """
              And the response header "X-EdFi-Warning" is absent

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 02 A resource matched by several collection elements is returned and counted once
             When a GET request is made to "/ed-fi/schools?identificationCode=C-200&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body should contain "IdCodeSchoolBravo"
              And the response headers include
                  """
                    {
                        "total-count": 1
                    }
                  """

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 03 A code and its system must match the same collection element
             When a GET request is made to "/ed-fi/schools?identificationCode=C-100&educationOrganizationIdentificationSystemDescriptor=uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor%23SEA&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body should contain "IdCodeSchoolEcho"
              And the response body should not contain "IdCodeSchoolAlpha"

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 04 A code and a system that occur only on different elements do not match
             When a GET request is made to "/ed-fi/schools?identificationCode=C-600&educationOrganizationIdentificationSystemDescriptor=uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor%23SEA&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response headers include
                  """
                    {
                        "total-count": 0
                    }
                  """

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 05 A schools filter excludes a school without identification codes
             When a GET request is made to "/ed-fi/schools?educationOrganizationIdentificationSystemDescriptor=uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor%23NCES&totalCount=true"
             Then it should respond with 200
              And total of records should be 3
              And the response body should contain "IdCodeSchoolAlpha"
              And the response body should contain "IdCodeSchoolBravo"
              And the response body should contain "IdCodeSchoolFoxtrot"
              And the response body should not contain "IdCodeSchoolCharlie"

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 06 A staff qualifier matches alone and only on the same element as the code
             When a GET request is made to "/ed-fi/staffs?assigningOrganizationIdentificationCode=O1&totalCount=true"
             Then it should respond with 200
              And total of records should be 2
             When a GET request is made to "/ed-fi/staffs?identificationCode=X&assigningOrganizationIdentificationCode=O1&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body should contain "IdCodeStaffAlpha"
              And the response body should not contain "IdCodeStaffBravo"

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 07 A descriptor that does not resolve returns an empty page
             When a GET request is made to "/ed-fi/schools?educationOrganizationIdentificationSystemDescriptor=uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor%23Unknown&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response headers include
                  """
                    {
                        "total-count": 0
                    }
                  """

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 08 A descriptor value without a fragment is looked up and returns an empty page
             When a GET request is made to "/ed-fi/schools?educationOrganizationIdentificationSystemDescriptor=uri://ed-fi.org/NoFragment&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response headers include
                  """
                    {
                        "total-count": 0
                    }
                  """

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 09 A descriptor value containing NUL is rejected
             When a GET request is made to "/ed-fi/schools?identificationCode=C-999&educationOrganizationIdentificationSystemDescriptor=uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor%23NCES%00"
             Then it should respond with 400
              And the response body should contain "Descriptor values must not contain the NUL character (U+0000) or unpaired surrogate characters."
              And the response body should contain "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor"

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 10 Course transcript root query fields keep their meaning
            Given the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "IdCodeStudent" | Student   | One         | 2010-01-01 |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2026       | true              | "2025-2026"           |
              And the system has these "courses"
                  | courseCode | identificationCodes                                                                                                                                  | educationOrganizationReference   | courseTitle | numberOfParts |
                  | IDC-ALG-1  | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code", "identificationCode": "IDC-ALG-1"} ] | {"educationOrganizationId": 8101} | Algebra I   | 1             |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "8101"
              And the system has these "studentSchoolAssociations"
                  | studentReference                       | schoolReference      | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "IdCodeStudent" } | { "schoolId": 8101 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade" | 2025-08-01 |
              And the system has these "studentAcademicRecords"
                  | studentReference                       | educationOrganizationReference    | schoolYearTypeReference | termDescriptor                                 |
                  | { "studentUniqueId": "IdCodeStudent" } | { "educationOrganizationId": 8101 } | {"schoolYear": 2026}    | "uri://ed-fi.org/TermDescriptor#Fall Semester" |
              And the system has these "courseTranscripts"
                  | courseReference                                         | studentAcademicRecordReference                                                                                                                  | courseAttemptResultDescriptor                          | courseCatalogURL                    | assigningOrganizationIdentificationCode | alternativeCourseIdentificationCodes                                                                                                                                                                                                                      |
                  | {"courseCode": "IDC-ALG-1", "educationOrganizationId": 8101} | {"educationOrganizationId": 8101, "schoolYear": 2026, "studentUniqueId": "IdCodeStudent", "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"} | "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass" | "https://root.example.org/catalog" | "ROOT-ORG"                              | [ {"identificationCode": "IDC-ALT-1", "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code", "courseCatalogURL": "https://item.example.org/catalog", "assigningOrganizationIdentificationCode": "ITEM-ORG"} ] |
             When a GET request is made to "/ed-fi/courseTranscripts?courseCatalogURL=https://root.example.org/catalog"
             Then it should respond with 200
              And total of records should be 1
             When a GET request is made to "/ed-fi/courseTranscripts?courseCatalogURL=https://item.example.org/catalog"
             Then it should respond with 200
              And total of records should be 0
             When a GET request is made to "/ed-fi/courseTranscripts?assigningOrganizationIdentificationCode=ITEM-ORG"
             Then it should respond with 200
              And total of records should be 0
             When a GET request is made to "/ed-fi/courseTranscripts?identificationCode=IDC-ALT-1"
             Then it should respond with 200
              And total of records should be 1
              And the response header "X-EdFi-Warning" is absent

        @e2e-ci-shard-2
        @MssqlRepresentative
        Scenario: 11 A resource without an identification-code collection still ignores the name with a warning
             When a GET request is made to "/ed-fi/students?identificationCode=C-100"
             Then it should respond with 200
              And the response headers include
                  """
                    {
                        "X-EdFi-Warning": "Ignored query parameters: identificationCode"
                    }
                  """
