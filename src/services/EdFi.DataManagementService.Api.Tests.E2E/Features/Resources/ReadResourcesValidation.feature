# This is a rough draft feature for future use.
Feature: Resources "Read Operation" Validations

        Background:
        # This might not be necessary - only keep it if other .feature files
        # are somehow bypassing token authorization. # Might need to provide
        # additional information here about # the allowed issuer and key
        # information for validating the signature.
            Given the Data Management Service must receive a token issued by "http://localhost"

        @ignore
        Scenario: Verify existing resources can be retrieved succesfully
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify retrieving a single resource by ID
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify response contains expected data
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify response code 404 when getting a different resource

        @ignore
        Scenario: Verify response code 404 when ID does not exist
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify empty array when there are not records on GET All
             When sending a GET request without headers to "/"
             Then it should respond with 200
