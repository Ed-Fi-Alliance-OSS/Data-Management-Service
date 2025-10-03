Feature: Synthetic Test - Cascade Update Kafka Validation

    Background:
        Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

    @kafka
    Scenario: 01 Update with a single referencing document should trigger multiple Kafka messages
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "Session" with identity "sessionName"
          And the schema has resource "CourseOffering" with identity "localCourseCode"
          And the "CourseOffering" resource has reference "sessionReference" to "Session"
         When the schema is deployed to the DMS
         When a POST request is made for dependent resource "/ed-fi/sessions" with
              """
              { "sessionName": "Third Quarter" }
              """
         Then it should respond with 201
          And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{dependentId}",
                    "sessionName": "Third Quarter"
                  }
                  """
         When a POST request is made to "/ed-fi/courseOfferings" with
              """
              {
                "localCourseCode": "ABC",
                "sessionReference": {
                  "sessionName": "Third Quarter"
                }
              }
              """
         Then it should respond with 201
        Given Kafka should be reachable
         Then Kafka consumer should be able to connect to topic "edfi.dms.document"
        Given I start collecting Kafka messages
         When a PUT request is made to "/ed-fi/sessions/{dependentId}" with
              """
              {
                "id": "{dependentId}",
                "sessionName": "Fourth Quarter"
              }
              """
         Then it should respond with 204         
          And multiple Kafka messages should be received with count 2
          And a Kafka message should have the deleted flag "false" and EdFiDoc
              """
              {
                "id": "{id}",
                "sessionName": "Fourth Quarter"
              }
              """


    @kafka
    Scenario: 02 Update with multiple referencing documents should trigger multiple Kafka messages        
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "GradingPeriod" with identity "gradingPeriodName"
          And the schema has resource "ReportCard" with identity "reportCardId"
          And the "ReportCard" resource has reference "gradingPeriodReference" to "GradingPeriod"
          And the schema has resource "Grade" with identity "gradeId"
          And the "Grade" resource has reference "gradingPeriodReference" to "GradingPeriod"
         When the schema is deployed to the DMS
         When a POST request is made for dependent resource "/ed-fi/gradingPeriods" with
              """
              { "gradingPeriodName": "First Semester" }
              """
         Then it should respond with 200 or 201
         When a POST request is made to "/ed-fi/reportCards" with
              """
              {
                "reportCardId": "RC001",
                "gradingPeriodReference": {
                  "gradingPeriodName": "First Semester"
                }
              }
              """
         Then it should respond with 200 or 201
         When a POST request is made to "/ed-fi/grades" with
              """
              {
                "gradeId": "G001",
                "gradingPeriodReference": {
                  "gradingPeriodName": "First Semester"
                }
              }
              """
         Then it should respond with 200 or 201
         Given Kafka should be reachable
          Then Kafka consumer should be able to connect to topic "edfi.dms.document"
        Given I start collecting Kafka messages
         When a PUT request is made to "/ed-fi/gradingPeriods/{dependentId}" with
              """
              {
                "id": "{dependentId}",
                "gradingPeriodName": "Second Semester"
              }
              """
         Then it should respond with 204
          And multiple Kafka messages should be received with count 3
          And a Kafka message should have the deleted flag "false" and EdFiDoc
              """
              {
                "id": "{id}",
                "gradingPeriodName": "Second Semester"
              }
              """

    @kafka
    Scenario: 03 Multi-level cascade update should trigger multiple Kafka messages
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "Session" with identity "sessionName"
          And the schema has resource "CourseOffering" with identity "localCourseCode"
          And the "CourseOffering" resource has reference "sessionReference" to "Session"
          And the schema has resource "Section" with identity "sectionIdentifier"
          And the "Section" resource has reference "courseOfferingReference" to "CourseOffering"
         When the schema is deployed to the DMS
         When a POST request is made for dependent resource "/ed-fi/sessions" with
              """
              { "sessionName": "Q1" }
              """
         Then it should respond with 200 or 201
         When a POST request is made to "/ed-fi/courseOfferings" with
              """
              {
                "localCourseCode": "MATH101",
                "sessionReference": {
                  "sessionName": "Q1"
                }
              }
              """
         Then it should respond with 200 or 201
         When a POST request is made to "/ed-fi/sections" with
              """
              {
                "sectionIdentifier": "SECTION-A",
                "courseOfferingReference": {
                  "localCourseCode": "MATH101"
                }
              }
              """
         Then it should respond with 200 or 201
        Given Kafka should be reachable
         Then Kafka consumer should be able to connect to topic "edfi.dms.document"
        Given I start collecting Kafka messages
         When a PUT request is made to "/ed-fi/sessions/{dependentId}" with
              """
              {
                "id": "{dependentId}",
                "sessionName": "Q2"
              }
              """
         Then it should respond with 204
          And multiple Kafka messages should be received with count 2
          And a Kafka message should have the deleted flag "false" and EdFiDoc
              """
              {
                "id": "{id}",
                "sessionName": "Q2"
              }
              """

    @kafka
    Scenario: 04 Identity update with cascading updates should preserve original identity in updated references
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "ClassPeriod" with identity "classPeriodName"
          And the schema has resource "BellSchedule" with identity "bellScheduleName"
          And the "BellSchedule" resource has reference "classPeriodReference" to "ClassPeriod"
         When the schema is deployed to the DMS
         When a POST request is made for dependent resource "/ed-fi/classPeriods" with
              """
              { "classPeriodName": "Third Period" }
              """
         Then it should respond with 200 or 201
         When a POST request is made to "/ed-fi/bellSchedules" with
              """
              {
                "bellScheduleName": "Schedule 1",
                "classPeriodReference": {
                  "classPeriodName": "Third Period"
                }
              }
              """
         Then it should respond with 200 or 201
        Given Kafka should be reachable
         Then Kafka consumer should be able to connect to topic "edfi.dms.document"
        Given I start collecting Kafka messages
         When a PUT request is made to "/ed-fi/classPeriods/{dependentId}" with
              """
              {
                "id": "{dependentId}",
                "classPeriodName": "Fourth Period"
              }
              """
         Then it should respond with 204
          And multiple Kafka messages should be received with count 2
          And a Kafka message should have the deleted flag "false" and EdFiDoc
              """
              {
                "id": "{id}",
                "classPeriodName": "Fourth Period"
              }
              """

    @kafka
    Scenario: 05 Cascade update with collection references should trigger multiple Kafka messages
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "ClassPeriod" with identity "classPeriodName"
          And the schema has resource "BellSchedule" with identity "bellScheduleName"
          And the "BellSchedule" resource has collection reference "classPeriods" to "ClassPeriod"
         When the schema is deployed to the DMS
         When a POST request is made for dependent resource "/ed-fi/classPeriods" with
              """
              { "classPeriodName": "First Period" }
              """
         Then it should respond with 200 or 201
         When a POST request is made to "/ed-fi/classPeriods" with
              """
              { "classPeriodName": "Second Period" }
              """
         Then it should respond with 200 or 201
         When a POST request is made to "/ed-fi/bellSchedules" with
              """
              {
                "bellScheduleName": "Daily Schedule",
                "classPeriods": [
                  {
                    "classPeriodReference": {
                      "classPeriodName": "First Period"
                    }
                  },
                  {
                    "classPeriodReference": {
                      "classPeriodName": "Second Period"
                    }
                  }
                ]
              }
              """
         Then it should respond with 200 or 201
        Given Kafka should be reachable
         Then Kafka consumer should be able to connect to topic "edfi.dms.document"
        Given I start collecting Kafka messages
         When a PUT request is made to "/ed-fi/classPeriods/{dependentId}" with
              """
              {
                "id": "{dependentId}",
                "classPeriodName": "Morning Period"
              }
              """
         Then it should respond with 204
          And multiple Kafka messages should be received with count 2
          And a Kafka message should have the deleted flag "false" and EdFiDoc
              """
              {
                "id": "{id}",
                "classPeriodName": "Morning Period"
              }
              """
