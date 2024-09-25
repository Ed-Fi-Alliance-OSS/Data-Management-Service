Feature: Query String handling for GET requests for Descriptor Queries

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

        @addwait
        Scenario: 00 Background
            Given the system has these descriptors
                  | descriptorValue                                           |
                  | uri://ed-fi.org/AbsenceEventCategoryDescriptor#Sick Leave |

        @API-115
        Scenario: 01 Verify existing descriptors can be retrieved successfully
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors"
             Then it should respond with 200
              And the response body is
                  """
                    [
                        {
                            "id": "{id}",
                            "codeValue": "Sick Leave",
                            "description": "Sick Leave",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave"
                        }
                    ]
                  """

        @API-116
        Scenario: 05 Ensure clients can retrieve a descriptor by requesting through a valid codeValue
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/?codeValue=Sick+Leave"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "id": "{id}",
                        "codeValue": "Sick Leave",
                        "description": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  ]
                  """

        @API-117
        Scenario: 06 Ensure clients cannot retrieve a descriptor by requesting through a non existing codeValue
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?codeValue=Test"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-118
        Scenario: 07 Ensure clients can retrieve a descriptor by requesting through a valid namespace
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?namespace=uri://ed-fi.org/AbsenceEventCategoryDescriptor#Sick Leave"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "id": "{id}",
                        "codeValue": "Sick Leave",
                        "description": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  ]
                  """

        @API-119
        Scenario: 08 Ensure clients cannot retrieve a descriptor by requesting through a non existing namespace
             When a GET request is made to "/ed-fi/disabilityDescriptors?namespace=uri://ed-fi.org/DisabilityDescriptor#Fake"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """
