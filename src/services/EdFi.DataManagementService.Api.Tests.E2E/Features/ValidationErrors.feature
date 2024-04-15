Feature: ValidationErrors
    POST or PUT a request that has an invalid payload.

Scenario: Post an empty request object
 When sending a POST request to "data/ed-fi/schools" with body
  """
  """
 Then the response code is 400
    And the response body is
"""
{"detail":"Data validation failed. See errors for details.","type":"urn:dms:bad-request:data","title":"Data Validation Error","status":400,"correlationId":null,"validationErrors":{},"errors":["A non-empty request body is required."]}
"""

Scenario: Post an invalid body for academicWeeks when weekIdentifier length should be at least 5 characters
 When sending a POST request to "data/ed-fi/academicWeeks" with body
 """
 {
  "weekIdentifier": "one",
  "schoolReference": {
    "schoolId": 17012391
  },
  "beginDate": "2023-09-11",
  "endDate": "2023-09-11",
  "totalInstructionalDays": 300
 }
 """ 
 	Then the response code is 400
    And the response body is
"""
{"detail":"Data validation failed. See validationErrors for details.","type":"urn:dms:bad-request:data","title":"Data Validation Error","status":400,"correlationId":null,"validationErrors":{"weekIdentifier : ":["weekIdentifier : Value should be at least 5 characters"]},"errors":[]}
"""

Scenario: Post an invalid body for academicWeeks missing schoolid for schoolReference and totalInstructionalDays
  When sending a POST request to "data/ed-fi/academicWeeks" with body
  """
  {
    "weekIdentifier": "seven",
    "schoolReference": {
    },
    "beginDate": "2023-09-11",
    "endDate": "2023-09-11"
  }
  """
	Then the response code is 400
    And the response body is
"""
{"detail":"Data validation failed. See validationErrors for details.","type":"urn:dms:bad-request:data","title":"Data Validation Error","status":400,"correlationId":null,"validationErrors":{"":["Required properties [\u0022totalInstructionalDays\u0022] are not present"],"schoolReference : ":["Required properties [\u0022totalInstructionalDays\u0022] are not present","schoolReference : Required properties [\u0022schoolId\u0022] are not present"]},"errors":[]}
"""

Scenario: Post a valid Descriptor
  When sending a POST request to "data/ed-fi/absenceEventCategoryDescriptors" with body
  """
  {
    "codeValue": "Sample",
    "shortDescription": "Bereavement",
    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor"
  }
  """
  Then the response code is 201

