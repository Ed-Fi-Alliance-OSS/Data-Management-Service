# This is a rough draft feature for future use.
Feature: Validate DELETE for Absence Event Category Descriptors


        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

        @ignore
        Scenario: Verify deleting a specific resource by ID
             When sending a POST request with valid body to
                  | Name          | Endpoint                               |
                  | academicWeeks | /ed-fi/absenceEventCategoryDescriptors |
             Then it should respond with 201
              And the record can be retrieved with a GET request

        @ignore
        Scenario: Verify error handling when deleting a non existing resource
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify response code when GET a deleted resource
             When sending a GET request without headers to "/"
             Then it should respond with 200
