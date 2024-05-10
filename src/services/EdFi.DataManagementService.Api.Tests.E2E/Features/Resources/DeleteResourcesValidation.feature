# This is a rough draft feature for future use.
@ignore
Feature: Resources "Delete" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And request made to "/ed-fi/absenceEventCategoryDescriptors" with
                  | setting          | value                                          |
                  | codeValue        | Sick Leave                                     |
                  | description      | Sick Leave                                     |
                  | namespace        | uri://ed-fi.org/AbsenceEventCategoryDescriptor |
                  | shortDescription | Sick Leave                                     |
             Then it should respond with 201                      |

        @ignore
        Scenario: Verify deleting a specific resource by ID
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 204

        @ignore
        Scenario: Verify error handling when deleting a non existing resource
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/00112233445566"
             Then it should respond with 400

        @ignore
        Scenario: Verify response code when GET a deleted resource
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should responde with 400
