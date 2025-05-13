Feature: TotalCount Response Header for GET Requests
        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        Scenario: 00 Background
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution                             | gradeLevels                                                                         | educationOrganizationCategories                                                                                        |
                  | 5         | School with max edorgId value                 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 6         | UT Austin College of Education Under Graduate | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901001 | Grand Bend High School                        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901044 | Grand Bend Middle School                      | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901045 | UT Austin Extended Campus                     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade"} ]  | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |

        @API-136
        Scenario: 01 Validate totalCount value when there are no matching schools in the Database
             When a GET request is made to "/ed-fi/schools?totalCount=true&nameOfInstitution=does+not+exist"
             Then it should respond with 200
              And the response headers include
                  """
                    {
                        "total-count": 0
                    }
                  """

        @API-137
        Scenario: 02 Validate totalCount is not included when there are no existing schools in the Database and value equals to false
             When a GET request is made to "/ed-fi/schools?totalCount=false"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-138
        Scenario: 03 Validate totalCount is not included when is not included in the URL
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-140
        Scenario: 05 Ensure that schools return the total count
             When a GET request is made to "/ed-fi/schools?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                    {
                        "total-count": 5
                    }
                  """

        @API-141
        Scenario: 06 Validate totalCount Header is not included when equals to false
             When a GET request is made to "/ed-fi/schools?totalCount=false"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-142
        Scenario: 07 Validate totalCount is not included when it is not present in the URL
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-143
        Scenario: 08 Ensure results can be limited and totalCount matches the actual number of existing records
             When a GET request is made to "/ed-fi/schools?totalCount=true&limit=2"
             Then getting less schools than the total-count
              And the response headers include
                  """
                    {
                        "total-count": 5
                    }
                  """
