Feature: Query String handling for GET requests for Descriptor Queries

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        Scenario: 00 Background
            Given the system has these descriptors
                  | descriptorValue                                           |
                  | uri://ed-fi.org/AbsenceEventCategoryDescriptor#Sick Leave |
              And the system has these "calendarEventDescriptors"
                  | codeValue | description | namespace                               | shortDescription | effectiveBeginDate | effectiveEndDate |
                  | Fake      | Fake        | uri://ed-fi.org/CalendarEventDescriptor | Fake             | 2020-01-01         | 2020-12-31       |

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

        Scenario: 09 Ensure clients can query by effectiveBeginDate and effectiveEndDate
             When a GET request is made to "/ed-fi/calendarEventDescriptors?effectiveBeginDate=2020-01-01"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "id": "{id}",
                        "codeValue": "Fake",
                        "description": "Fake",
                        "namespace": "uri://ed-fi.org/CalendarEventDescriptor",
                        "shortDescription": "Fake",
                        "effectiveBeginDate": "2020-01-01",
                        "effectiveEndDate": "2020-12-31"
                    }
                  ]
                  """
             When a GET request is made to "/ed-fi/calendarEventDescriptors?effectiveEndDate=2020-12-31"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "id": "{id}",
                        "codeValue": "Fake",
                        "description": "Fake",
                        "namespace": "uri://ed-fi.org/CalendarEventDescriptor",
                        "shortDescription": "Fake",
                        "effectiveBeginDate": "2020-01-01",
                        "effectiveEndDate": "2020-12-31"
                    }
                  ]
                  """
             When a GET request is made to "/ed-fi/calendarEventDescriptors?effectiveEndDate=1920-12-31"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """
