Feature: ValidationErrors
    POST a request that has an invalid payload.

Scenario: Post an empty request object
 When sending a POST request to "data/ed-fi/schools" with body
  """
  """
 Then the response code is 400
    And the response body is
 """
{"detail":"The request could not be processed. See 'errors' for details.","type":"urn:ed-fi:api:bad-request","title":"Bad Request","status":400,"correlationId":null,"validationErrors":{},"errors":["A non-empty request body is required."]}
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
{"detail":"Data validation failed. See 'validationErrors' for details.","type":"urn:ed-fi:api:bad-request:data","title":"Data Validation Failed","status":400,"correlationId":null,"validationErrors":{"$.weekIdentifier : ":["weekIdentifier : Value should be at least 5 characters"]},"errors":[]}
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
{"detail":"Data validation failed. See 'validationErrors' for details.","type":"urn:ed-fi:api:bad-request:data","title":"Data Validation Failed","status":400,"correlationId":null,"validationErrors":{"$.totalInstructionalDays":["totalInstructionalDays is required."],"$.schoolReference.schoolId":["schoolId is required."]},"errors":[]}
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

Scenario: Post an invalid body for academicWeeks missing more than one required field
  When sending a POST request to "data/ed-fi/academicWeeks" with body
  """
  {
    "_weekIdentifier": "abcdef",
    "_schoolReference": {
        "schoolId": 255901001
    },
    "_beginDate": "2024-04-04",
    "_endDate": "2024-04-04",
    "_totalInstructionalDays": 300
  }
  """
  Then the response code is 400
    And the response body is
"""
{"detail":"Data validation failed. See 'validationErrors' for details.","type":"urn:ed-fi:api:bad-request:data","title":"Data Validation Failed","status":400,"correlationId":null,"validationErrors":{"$.schoolReference":["schoolReference is required."],"$.weekIdentifier":["weekIdentifier is required."],"$.beginDate":["beginDate is required."],"$.endDate":["endDate is required."],"$.totalInstructionalDays":["totalInstructionalDays is required."],"$._schoolReference : ":["_schoolReference : All values fail against the false schema"],"$._beginDate : ":["_beginDate : All values fail against the false schema"],"$._endDate : ":["_endDate : All values fail against the false schema"],"$._totalInstructionalDays : ":["_totalInstructionalDays : All values fail against the false schema"]},"errors":[]}
"""

Scenario: Post an invalid body for academicWeeks missing a required field in a nested object schoolid for schoolReference
  When sending a POST request to "data/ed-fi/academicWeeks" with body
  """
  {
    "weekIdentifier": "abcdef",
    "schoolReference": {
        "_schoolId": 255901001
    },
    "beginDate": "2024-04-04",
    "endDate": "2024-04-04",
    "totalInstructionalDays": 300
  }
  """
  Then the response code is 400
    And the response body is
"""
{"detail":"Data validation failed. See 'validationErrors' for details.","type":"urn:ed-fi:api:bad-request:data","title":"Data Validation Failed","status":400,"correlationId":null,"validationErrors":{"$.schoolReference.schoolId":["schoolId is required."]},"errors":[]}
"""

Scenario: Post an invalid body for academicWeeks missing a comma before beginDate
  When sending a POST request to "data/ed-fi/academicWeeks" with body
  """
  {
    "weekIdentifier": "abcdef",
    "schoolReference": {
        "schoolId": 255901001
    }
    "beginDate": "2024-04-04",
    "endDate": "2024-04-04",
    "totalInstructionalDays": 300
  }
  """
  Then the response code is 400
    And the response body is
"""
{"detail":"Data validation failed. See 'validationErrors' for details.","type":"urn:ed-fi:api:bad-request:data","title":"Data Validation Failed","status":400,"correlationId":null,"validationErrors":{"$.":["'\"' is invalid after a value. Expected either ',', '}', or ']'. LineNumber: 5 | BytePositionInLine: 2."]},"errors":[]}
"""

Scenario: Post an invalid body for courseOfferings missing a two required fields for a nested object CourseReference and also schoolReference
  When sending a POST request to "data/ed-fi/courseOfferings" with body
  """
  {
    "localCourseCode": "1",
    "courseReference": {
        "_courseCode": "1",
        "_educationOrganizationId": 1
    },
     "_schoolReference": {
        "schoolId": 255901001
    },
    "sessionReference": {
        "schoolId": 255901001,
        "schoolYear": 2022,
        "sessionName": "Test"
    }
}
  """
  Then the response code is 400
    And the response body is
"""
{"detail":"Data validation failed. See 'validationErrors' for details.","type":"urn:ed-fi:api:bad-request:data","title":"Data Validation Failed","status":400,"correlationId":null,"validationErrors":{"$.schoolReference":["schoolReference is required."],"$.courseReference.courseCode":["courseCode is required."],"$.courseReference.educationOrganizationId":["educationOrganizationId is required."],"$._courseCode : ":["_courseCode : All values fail against the false schema"],"$._educationOrganizationId : ":["_educationOrganizationId : All values fail against the false schema"]},"errors":[]}
"""
