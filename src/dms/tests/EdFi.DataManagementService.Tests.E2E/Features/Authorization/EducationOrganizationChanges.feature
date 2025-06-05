Feature: EducationOrganizationChanges Authorization

    Rule: When a school is moved to a new LEA, make sure the old LEA can't access the student, contact, or staff anymore, and the new LEA can access them.
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 255901                 | Grand Bend ISD    | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 255902                 | Pflugerville ISD  | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | _storeResultingIdInVariable | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference       |
                  | SchoolId1                   | 255901001 | Grand Bend High School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 255901} |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | StudentId1                  | "61"            | student-fn | student-ln  | 2008-01-01 |
                  | StudentId2                  | "62"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable |  | studentReference            | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentSchoolAssociationId1 |  | { "studentUniqueId": "61" } | { "schoolId": 255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId2 |  | { "studentUniqueId": "62" } | { "schoolId": 255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName          | lastSurname |
                  | ContactId1                  | "91111"         | Authorized contact | contact-ln  |
              And the system has these "studentContactAssociations"
                  | studentReference            | contactReference               | emergencyContactStatus |
                  | { "studentUniqueId": "61" } | { "contactUniqueId": "91111" } | "true"                 |
              And the system has these "Staffs"
                  | _storeResultingIdInVariable | staffUniqueId | firstName | lastSurname |
                  | StaffId1                    | s0001         | peterson  | Buck        |
              And the system has these "staffEducationOrganizationAssignmentAssociations"
                  | beginDate  | staffClassificationDescriptor                         | educationOrganizationReference           | staffReference                 |
                  | 10/10/2020 | uri://ed-fi.org/StaffClassificationDescriptor#Teacher | { "educationOrganizationId": 255901001 } | {  "staffUniqueId": "s0001"  } |
              And the system has these "staffSchoolAssociations"
                  | _storeResultingIdInVariable | staffReference               | schoolReference           | programAssignmentDescriptor                                     |
                  | staffSchoolAssociationId1   | { "staffUniqueId": "s0001" } | { "schoolId": 255901001 } | "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education" |


        Scenario: 01 Ensure client can access the Student ,the Contact and the Staff with a  Grand Bend ISD School 255901
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a GET request is made to "/ed-fi/schools/{SchoolId1}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/students/{StudentId1}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                        "id": "{StudentId1}",
                        "birthDate": "2008-01-01",
                        "firstName": "student-fn",
                        "lastSurname": "student-ln",
                        "studentUniqueId": "61"
                      }
                  """

             When a GET request is made to "/ed-fi/contacts/{ContactId1}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{ContactId1}",
                      "firstName": "Authorized contact",
                      "lastSurname": "contact-ln",
                      "contactUniqueId": "91111"
                  }
                  """

             When a GET request is made to "/ed-fi/Staffs/{StaffId1}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": "{StaffId1}",
                    "firstName": "peterson",
                    "lastSurname": "Buck",
                    "staffUniqueId": "s0001"
                  }
                  """

        Scenario: 02 Ensure client can't access the Student anymore when a School gets updated to a different LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a PUT request is made to "/ed-fi/schools/{SchoolId1}" with
                  """
                  {
                    "id": "{SchoolId1}",
                    "schoolId": "255901001",
                    "nameOfInstitution": "Grand Bend ISD",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                    ],
                    "localEducationAgencyReference": {
                          "localEducationAgencyId": 255902
                      }
                  }
                  """
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a GET request is made to "/ed-fi/students/{StudentId1}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "correlationId": "0HND34OBOV9J1:0000001E",
                     "validationErrors": {},
                     "errors": [
                     "No relationships have been established between the caller's education organization id claims ('255901') and the resource item's StudentUniqueId value."
                     ]
                  }
                  """

        Scenario: 03 Ensure client can't access the  Contact anymore when a student  gets updated to a different school
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a POST request is made to "/ed-fi/schools" with
                  """
                    {
                      "schoolId": 255901002,
                      "nameOfInstitution": "Grand Bend High School",
                      "gradeLevels": [
                        {
                          "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                      ],
                      "educationOrganizationCategories": [
                        {
                          "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                      ],
                      "localEducationAgencyReference": {
                        "localEducationAgencyId": 255901
                      }
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId1}" with
                  """
                  {
                      "id": "{StudentSchoolAssociationId1}",
                      "studentReference": {
                        "studentUniqueId": "61"
                      },
                      "schoolReference": {
                        "schoolId": 255901002
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "entryDate": "2023-08-01"
                  }
                  """
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a GET request is made to "/ed-fi/contacts/{ContactId1}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "correlationId": "0HND34OBOV9J1:0000001E",
                     "validationErrors": {},
                     "errors": [
                     "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's ContactUniqueId value."
                     ]
                  }
                  """

        Scenario: 04 Ensure client can't access the  Staffs anymore when a staff gets updated to a different school
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a PUT request is made to "/ed-fi/staffSchoolAssociations/{staffSchoolAssociationId1}" with
                  """
                     {
                       "id": "{staffSchoolAssociationId1}",
                        "schoolReference": {
                            "schoolId": 255901001
                        },
                        "staffReference": {
                            "staffUniqueId": "s0001"
                        },
                        "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
                    }
                  """
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a GET request is made to "/ed-fi/staffs/{StaffId1}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "correlationId": "0HND34OBOV9J1:0000001E",
                     "validationErrors": {},
                     "errors": [
                     "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's StaffUniqueId value."
                     ]
                  }
                  """

    Rule: When a school is removed from an LEA, make sure the LEA can no longer access the student, contact, or staff linked to that school.
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 255901                 | Grand Bend ISD    | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 255902                 | Pflugerville ISD  | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | _storeResultingIdInVariable | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference       |
                  | SchoolId1                   | 255901001 | Grand Bend High School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 255901} |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | StudentId1                  | "61"            | student-fn | student-ln  | 2008-01-01 |
                  | StudentId2                  | "62"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable |  | studentReference            | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentSchoolAssociationId1 |  | { "studentUniqueId": "61" } | { "schoolId": 255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId2 |  | { "studentUniqueId": "62" } | { "schoolId": 255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName          | lastSurname |
                  | ContactId1                  | "91111"         | Authorized contact | contact-ln  |
              And the system has these "studentContactAssociations"
                  | studentReference            | contactReference               | emergencyContactStatus |
                  | { "studentUniqueId": "61" } | { "contactUniqueId": "91111" } | "true"                 |
              And the system has these "Staffs"
                  | _storeResultingIdInVariable | staffUniqueId | firstName | lastSurname |
                  | StaffId1                    | s0001         | peterson  | Buck        |
              And the system has these "staffEducationOrganizationAssignmentAssociations"
                  | beginDate  | staffClassificationDescriptor                         | educationOrganizationReference           | staffReference                 |
                  | 10/10/2020 | uri://ed-fi.org/StaffClassificationDescriptor#Teacher | { "educationOrganizationId": 255901001 } | {  "staffUniqueId": "s0001"  } |
              And the system has these "staffSchoolAssociations"
                  | _storeResultingIdInVariable | staffReference               | schoolReference           | programAssignmentDescriptor                                     |
                  | staffSchoolAssociationId1   | { "staffUniqueId": "s0001" } | { "schoolId": 255901001 } | "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education" |

        Scenario: 05 Ensure client can't access the Student anymore when a School updated to remove LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a PUT request is made to "/ed-fi/schools/{SchoolId1}" with
                  """
                  {
                    "id": "{SchoolId1}",
                    "schoolId": "255901001",
                    "nameOfInstitution": "Grand Bend ISD",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                    ]
                  }
                  """
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a GET request is made to "/ed-fi/students/{StudentId1}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "correlationId": "0HND34OBOV9J1:0000001E",
                     "validationErrors": {},
                     "errors": [
                     "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's StudentUniqueId value."
                     ]
                  }
                  """

        Scenario: 06 Ensure client can't access the Contact anymore when a School updated to remove LEA

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a PUT request is made to "/ed-fi/schools/{SchoolId1}" with
                  """
                  {
                    "id": "{SchoolId1}",
                    "schoolId": "255901001",
                    "nameOfInstitution": "Grand Bend ISD",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                    ]
                  }
                  """
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a GET request is made to "/ed-fi/contacts/{ContactId1}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "correlationId": "0HND34OBOV9J1:0000001E",
                     "validationErrors": {},
                     "errors": [
                     "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's ContactUniqueId value."
                     ]
                  }
                  """

        Scenario: 07 Ensure client can't access the staff anymore when a School updated to remove LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a PUT request is made to "/ed-fi/schools/{SchoolId1}" with
                  """
                  {
                    "id": "{SchoolId1}",
                    "schoolId": "255901001",
                    "nameOfInstitution": "Grand Bend ISD",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                    ]
                  }
                  """
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a GET request is made to "/ed-fi/staffs/{StaffId1}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "correlationId": "0HND34OBOV9J1:0000001E",
                     "validationErrors": {},
                     "errors": [
                     "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's StaffUniqueId value."
                     ]
                  }
                  """

    Rule: When a school is added with new LEA, make sure the LEA can access the student, contact, or staff linked to that school.
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 255901                 | Grand Bend ISD    | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 255902                 | Pflugerville ISD  | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | _storeResultingIdInVariable | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | SchoolId1                   | 255901001 | Grand Bend High School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | StudentId1                  | "61"            | student-fn | student-ln  | 2008-01-01 |
                  | StudentId2                  | "62"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName          | lastSurname |
                  | ContactId1                  | "91111"         | Authorized contact | contact-ln  |
              And the system has these "Staffs"
                  | _storeResultingIdInVariable | staffUniqueId | firstName | lastSurname |
                  | StaffId1                    | s0001         | peterson  | Buck        |

        Scenario: 08 Ensure client can  access the Student  when a School updated to new LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a PUT request is made to "/ed-fi/schools/{SchoolId1}" with
                  """
                  {
                   "id": "{SchoolId1}",
                    "schoolId": "255901001",
                    "nameOfInstitution": "Grand Bend ISD",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                    ],
                    "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      }
                  }
                  """
             Then it should respond with 204

             When a POST request is made to "/ed-fi/studentSchoolAssociations/" with
                  """
                  {
                      "studentReference": {
                        "studentUniqueId": "61"
                      },
                      "schoolReference": {
                        "schoolId": 255901001
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "entryDate": "2023-08-01"
                  }
                  """
             Then it should respond with 201 or 200

             When a GET request is made to "/ed-fi/students/{StudentId1}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                        "id": "{StudentId1}",
                        "birthDate": "2008-01-01",
                        "firstName": "student-fn",
                        "lastSurname": "student-ln",
                        "studentUniqueId": "61"
                      }
                  """

        Scenario: 09 Ensure client can  access the contact  when a School updated to new LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a PUT request is made to "/ed-fi/schools/{SchoolId1}" with
                  """
                  {
                   "id": "{SchoolId1}",
                    "schoolId": "255901001",
                    "nameOfInstitution": "Grand Bend ISD",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                    ],
                    "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      }
                  }
                  """
             Then it should respond with 204

             When a POST request is made to "/ed-fi/studentSchoolAssociations/" with
                  """
                  {
                      "studentReference": {
                        "studentUniqueId": "61"
                      },
                      "schoolReference": {
                        "schoolId": 255901001
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "entryDate": "2023-08-01"
                  }
                  """
             Then it should respond with 201 or 200


             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "studentReference": {
                          "studentUniqueId": "61"
                      },
                      "contactReference": {
                          "contactUniqueId": "91111"
                      }
                  }
                  """
             Then it should respond with 201 or 200

             When a GET request is made to "/ed-fi/contacts/{ContactId1}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{ContactId1}",
                      "firstName": "Authorized contact",
                      "lastSurname": "contact-ln",
                      "contactUniqueId": "91111"
                  }
                  """
        Scenario: 10 Ensure client can  access the staff  when a School updated to new LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a PUT request is made to "/ed-fi/schools/{SchoolId1}" with
                  """
                  {
                   "id": "{SchoolId1}",
                    "schoolId": "255901001",
                    "nameOfInstitution": "Grand Bend ISD",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                        }
                    ],
                    "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      }
                  }
                  """
             Then it should respond with 204

             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                      "staffReference": {  "staffUniqueId": "s0001"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                     {
                        "schoolReference": {
                            "schoolId": 255901001
                        },
                        "staffReference": {
                            "staffUniqueId": "s0001"
                        },
                        "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
                    }
                  """
             Then it should respond with 201 or 200

             When a GET request is made to "/ed-fi/Staffs/{StaffId1}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": "{StaffId1}",
                    "firstName": "peterson",
                    "lastSurname": "Buck",
                    "staffUniqueId": "s0001"
                  }
                  """
                  

