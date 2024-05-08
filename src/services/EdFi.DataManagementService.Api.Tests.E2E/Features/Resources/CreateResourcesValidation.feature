# This is a rough draft feature for future use.
@ignore
Feature: Validate Creation for Absence Event Category Descriptors

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

        @ignore
        Scenario: Verify new resource can be created successfully
             When sending a POST request with valid body to
                  | Name          | Endpoint                               |
                  | academicWeeks | /ed-fi/absenceEventCategoryDescriptors |
             Then it should respond with 201
              And the record can be retrieved with a GET request

        @ignore
        Scenario: Verify error handling with POST using invalid data
             When sending a POST request with invalid body to
                  | Name          | Endpoint                               |
                  | academicWeeks | /ed-fi/absenceEventCategoryDescriptors |
             Then it should respond with 400

        @ignore
        Scenario: Verify error handling with POST using empty body
             When sending a POST request with empty body to
                  | Name          | Endpoint                               |
                  | academicWeeks | /ed-fi/absenceEventCategoryDescriptors |
             Then it should respond with 400

        @ignore
        Scenario: Verify POST of existing record without changes
             When sending a POST request with valid body of an existing record to
                  | Name          | Endpoint                               |
                  | academicWeeks | /ed-fi/absenceEventCategoryDescriptors |
             Then it should respond with 200
              And the record can be retrieved with a GET request

        @ignore
        Scenario: Verify POST of existing record (change non-key field) works
             When sending a POST request with valid body and existing description
                  | Name          | Endpoint                               |
                  | academicWeeks | /ed-fi/absenceEventCategoryDescriptors |
             Then it should respond with 200
