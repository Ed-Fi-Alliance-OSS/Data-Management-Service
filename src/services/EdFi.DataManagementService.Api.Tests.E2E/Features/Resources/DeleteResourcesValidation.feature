# This is a rough draft feature for future use.
Feature: Resources "Delete Operation" Validations

        Background:
    # This might not be necessary - only keep it if other .feature files
    # are somehow bypassing token authorization. # Might need to provide
    # additional information here about # the allowed issuer and key
    # information for validating the signature.
            Given the Data Management Service must receive a token issued by "http://localhost"

        @ignore
        Scenario: Verify deleting a specific resource by ID
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify error handling when deleting a non existing resource
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify response code when GET a deleted resource
             When sending a GET request without headers to "/"
             Then it should respond with 200
