Feature: Reject client requests for abstract entities

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        @API-053
        @relational-backend
        Scenario: 01 Ensure that clients cannot POST an abstract entity (Education Organizations)
             When a POST request is made to "/ed-fi/educationOrganizations" with
                  """
                  {
                  "educationOrganizationId": 1
                  }
                  """
             Then it should respond with 404

        @API-054
        @relational-backend
        Scenario: 02 Ensure that clients cannot POST an abstract entity (General Student Program Association)
             When a POST request is made to "/ed-fi/generalStudentProgramAssociations" with
                  """
                  {
                  "generalStudentProgramAssociationId": 1
                  }
                  """
             Then it should respond with 404

        @API-055
        @relational-backend
        Scenario: 03 Ensure that clients cannot GET an abstract entity (Education Organizations)
             When a GET request is made to "/ed-fi/educationOrganizations"
             Then it should respond with 404

        @API-056
        @relational-backend
        Scenario: 04 Ensure that clients cannot GET an abstract entity (Student Program Association)
             When a GET request is made to "/ed-fi/generalStudentProgramAssociations"
             Then it should respond with 404
