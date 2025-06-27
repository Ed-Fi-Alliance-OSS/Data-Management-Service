Feature: Synthetic Test - Document Array Validation

    Background:
        Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

    Scenario: 01 Validate resource with multiple references
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "GradingPeriod" with identity "gradingPeriodName"
          And the schema has resource "Student" with identity "studentUniqueId"
          And the schema has resource "Grade" with identities
              | identity |
              | studentUniqueId |
              | gradingPeriodName |
        When the schema is deployed to the DMS
          And a POST request is made to "/ed-fi/gradingPeriods" with
              """
              { "gradingPeriodName": "Q1" }
              """
        Then it should respond with 200 or 201
        When a POST request is made to "/ed-fi/gradingPeriods" with
              """
              { "gradingPeriodName": "Q2" }
              """
        Then it should respond with 200 or 201
        When a POST request is made to "/ed-fi/students" with
              """
              { "studentUniqueId": "12345" }
              """
        Then it should respond with 200 or 201
        When a POST request is made to "/ed-fi/grades" with
              """
              {
                "studentUniqueId": "12345",
                "gradingPeriodName": "Q1"
              }
              """
        Then it should respond with 200 or 201

    Scenario: 02 Validate simple resource with reference
        Given a synthetic core schema with project "Ed-Fi"
          And the schema has resource "GradingPeriod" with identity "gradingPeriodName"
          And the schema has resource "ReportCard" with identity "reportCardId"
          And the "ReportCard" resource has reference "gradingPeriodReference" to "GradingPeriod"
        When the schema is deployed to the DMS
          And a POST request is made to "/ed-fi/gradingPeriods" with
              """
              { "gradingPeriodName": "Q1" }
              """
        Then it should respond with 200 or 201
        When a POST request is made to "/ed-fi/reportCards" with
              """
              { 
                "reportCardId": "RC001",
                "gradingPeriodReference": {
                  "gradingPeriodName": "Q1"
                }
              }
              """
        Then it should respond with 200 or 201
        When a POST request is made to "/ed-fi/reportCards" with
              """
              { 
                "reportCardId": "RC002",
                "gradingPeriodReference": {
                  "gradingPeriodName": "Q999"
                }
              }
              """
        Then it should respond with 409
          And the response body is
              """
              {
                "detail": "The referenced GradingPeriod item(s) do not exist.",
                "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                "title": "Unresolved Reference",
                "status": 409,
                "correlationId": null,
                "validationErrors": {},
                "errors": []
              }
              """