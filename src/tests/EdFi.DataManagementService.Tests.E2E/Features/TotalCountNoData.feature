Feature: Query Strings handling for GET requests

    Background:
        Given there are no schools

        Scenario: Validate totalCount value when there are no existing schools in the Database
             When a GET request is made to "/ed-fi/schools?totalCount=true"
             Then it should respond with 200
              And the response headers includes total-count 0

        Scenario: Validate totalCount is not included when there are no existing schools in the Database and value equals to false
             When a GET request is made to "/ed-fi/schools?totalCount=false"
             Then it should respond with 200
              And the response headers does not include total-count

        Scenario: Validate totalCount is not included when is not included in the URL
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 200
              And the response headers does not include total-count
