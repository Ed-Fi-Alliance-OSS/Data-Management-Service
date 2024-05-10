# This is a rough draft feature for future use.
@ignore
Feature: Resources "Read" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And request made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value                                          |
                  | codeValue        | Sick Leave                                     |
                  | description      | Sick Leave                                     |
                  | namespace        | uri://ed-fi.org/AbsenceEventCategoryDescriptor |
                  | shortDescription | Sick Leave                                     |
             Then it should respond with 201

        @ignore
        Scenario: Verify existing resources can be retrieved succesfully
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors"
             Then it should respond with 200

        @ignore
        Scenario: Verify retrieving a single resource by ID
        # Replace Endpoint with the required value, this is just an example to make sure Descriptors are working fine
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/16435704274945eeae6c6c45b281aad3"
             Then it should respond with 200

        @ignore
        Scenario: Verify response contains expected data
        # Replace Endpoint with the required value, this is just an example to make sure Descriptors are working fine
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/16435704274945eeae6c6c45b281aad3"
             Then it should respond with 200
              And the response message includes:
                  | codeValue        | Jury duty                                      |
                  | description      | Jury duty                                      |
                  | namespace        | uri://ed-fi.org/AbsenceEventCategoryDescriptor |
                  | shortDescription | Jury duty                                      |

        @ignore
        Scenario: Verify response code 404 when ID does not exist
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/123123123123"
             Then it should respond with 404

        @ignore
        Scenario: Verify empty array when there are not records on GET All
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors"
             Then it should respond with 200
              And total of records should be "0"
