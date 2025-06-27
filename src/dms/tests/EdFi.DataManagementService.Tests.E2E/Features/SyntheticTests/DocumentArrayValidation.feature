Feature: Synthetic Test - Document Array Validation

    Background:
        Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

    Scenario: 01 Validate array of document references with duplicates
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "Course" with identity "courseCode"
          And the schema has resource "Student" with identity "studentUniqueId"
          And the schema has resource "CourseTranscript" with identities
              | identity |
              | studentUniqueId |
              | courseCode |
        When the schema is deployed to the DMS
          And a POST request is made to "/ed-fi/courses" with
              """
              { "courseCode": "MATH101" }
              """
        Then it should respond with 201
        When a POST request is made to "/ed-fi/courses" with
              """
              { "courseCode": "ENG101" }
              """
        Then it should respond with 201
        When a POST request is made to "/ed-fi/students" with
              """
              { "studentUniqueId": "12345" }
              """
        Then it should respond with 201
        When a POST request is made to "/ed-fi/courseTranscripts" with
              """
              {
                "studentUniqueId": "12345",
                "courseCode": "MATH101"
              }
              """
        Then it should respond with 201

    Scenario: 02 Validate simple resource with reference
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "School" with identity "schoolId"
          And the schema has resource "Student" with identity "studentUniqueId"
          And the "Student" resource has reference "schoolReference" to "School"
        When the schema is deployed to the DMS
          And a POST request is made to "/ed-fi/schools" with
              """
              { "schoolId": 100 }
              """
        Then it should respond with 201
        When a POST request is made to "/ed-fi/students" with
              """
              { 
                "studentUniqueId": "98765",
                "schoolReference": {
                  "schoolId": 100
                }
              }
              """
        Then it should respond with 201
        When a POST request is made to "/ed-fi/students" with
              """
              { 
                "studentUniqueId": "98766",
                "schoolReference": {
                  "schoolId": 999
                }
              }
              """
        Then it should respond with 400
          And the response body is
              """
              {
                "detail": "Data validation failed. See 'validationErrors' for details.",
                "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                "title": "Data Validation Failed",
                "status": 400,
                "correlationId": null,
                "validationErrors": {
                  "$.schoolReference.schoolId": [
                    "Reference 'School' with identity { SchoolId = 999 } does not exist."
                  ]
                },
                "errors": []
              }
              """