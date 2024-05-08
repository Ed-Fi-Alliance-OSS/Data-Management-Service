# This is a rough draft feature for future use.
@ignore
Feature: Resources "Update Operation" Validations

        Background:
    # This might not be necessary - only keep it if other .feature files
    # are somehow bypassing token authorization. # Might need to provide
    # additional information here about # the allowed issuer and key
    # information for validating the signature.
            Given the Data Management Service must receive a token issued by "http://localhost"

        @ignore
        Scenario: Verify that existing resources can be updated succesfully
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify updating a resource with valid data
             When sending a GET request without headers to "/"
             Then it should respond with 200

        @ignore
        Scenario: Verify error handling updating a resource with invalid data
             When sending a GET request without headers to "/"
             Then it should respond with 400

        @ignore
        Scenario: Verify that response contains the updated resource ID and data
             When sending a GET request without headers to "/"
             Then it should respond with 400

        @ignore
        Scenario: Verify error handling when updating a resoruce with empty body
             When sending a GET request without headers to "/"
             Then it should respond with 400

        @ignore
        Scenario: Verify error handling when resource ID is different in body on PUT
             When sending a GET request without headers to "/"
             Then it should respond with 400

        @ignore
        Scenario: Verify error handling when resource ID is not included in body on PUT
             When sending a GET request without headers to "/"
             Then it should respond with 400

        @ignore
        Scenario: Verify error handling when resource ID is included in body on POST
             When sending a GET request without headers to "/"
             Then it should respond with 400


