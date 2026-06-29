Feature: The Change Queries availableChangeVersions endpoint reports the oldest and newest change versions available in the DMS.

        @API-261
        @e2e-ci-shard-4
        Scenario: 01 GET availableChangeVersions returns the ODS-compatible contract when authenticated
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "oldestChangeVersion" should have value "0"
              And the response body path "newestChangeVersion" should be a non-negative integer

        @API-262
        @e2e-ci-shard-4
        Scenario: 02 newestChangeVersion increases after a resource is created
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored as variable "previousChangeVersion"
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
                      "codeValue": "CV-1184",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Available Change Versions Test"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" should be greater than variable "previousChangeVersion"

        @API-263
        @e2e-ci-shard-4
        Scenario: 03 GET availableChangeVersions requires authentication
            Given there is no Authorization header
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 401

        @API-264
        @e2e-ci-shard-4
        Scenario: 04 GET availableChangeVersions ignores query string parameters
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/changeQueries/v1/availableChangeVersions?minChangeVersion=1&foo=bar"
             Then it should respond with 200
              And the response body path "oldestChangeVersion" should have value "0"
              And the response body path "newestChangeVersion" should be a non-negative integer
