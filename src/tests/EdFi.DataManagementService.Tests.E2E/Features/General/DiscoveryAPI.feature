Feature: The Discovery API provides information about the application version, supported data model(s), and URLs for additional metadata.

        @API-062
        Scenario: 01 GET / returns the root Discovery API document
             When a GET request is made to "/"
             Then it should respond with 200
              And the general response body is
                  """
                  {
                      "version": "1.0.0",
                      "applicationName": "Ed-Fi Alliance Data Management Service",
                      "dataModels": [
                          {
                              "name": "Ed-Fi",
                              "version": "5.1.0",
                              "informationalVersion": "The Ed-Fi Data Standard v5.1"
                          }
                      ],
                      "urls": {
                          "dependencies": "{BASE_URL}/metadata/dependencies",
                          "openApiMetadata": "{BASE_URL}/metadata/specifications",
                          "oauth": "{BASE_URL}/oauth/token",
                          "dataManagementApi": "{BASE_URL}/data",
                          "xsdMetadata": "{BASE_URL}/metadata/xsd"
                      }
                  }
                  """

        @API-063
        Scenario: 02 GET /metadata returns the metadata URL list
             When a GET request is made to "/metadata"
             Then it should respond with 200
              And the general response body is
                  """
                  {
                      "dependencies": "{BASE_URL}/metadata/dependencies",
                      "specifications": "{BASE_URL}/metadata/specifications",
                      "xsdMetadata": "{BASE_URL}/metadata/xsd"
                  }
                  """

        @API-064
        Scenario: 03 GET /metadata/specifications returns the list of supported API specifications
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the general response body is
                  """
                  [
                      {
                          "name": "Resources",
                          "endpointUri": "{BASE_URL}/metadata/specifications/resources-spec.json",
                          "prefix": ""
                      },
                      {
                          "name": "Descriptors",
                          "endpointUri": "{BASE_URL}/metadata/specifications/descriptors-spec.json",
                          "prefix": ""
                      },
                      {
                          "name": "Discovery",
                          "endpointUri": "{BASE_URL}/metadata/specifications/discovery-spec.json",
                          "prefix": "Other"
                      }
                  ]
                  """

        @API-065
        Scenario: 04 GET /metadata/dependencies returns the dependency order for loading documents
             When a GET request is made to "/metadata/dependencies"
             Then it should respond with 200
              And there is a JSON file in the response body with a list of dependencies
