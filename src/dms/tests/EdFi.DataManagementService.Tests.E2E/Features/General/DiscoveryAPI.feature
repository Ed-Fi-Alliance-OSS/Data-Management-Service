Feature: The Discovery API provides information about the application version, supported data model(s), and URLs for additional metadata.

        @API-062
        Scenario: 01 GET / returns the root Discovery API document
             When a GET request is made to "/"
             Then it should respond with 200
              And the discovery API root response body is
                  """
                  {
                    "applicationName": "Ed-Fi Alliance Data Management Service",
                    "informationalVersion": "Release Candidate 1",
                    "dataModels": [
                      {
                        "name": "Ed-Fi",
                        "version": "5.2.0",
                        "informationalVersion": "The Ed-Fi Data Standard v5.2.0"
                      },
                      {
                        "name": "Homograph",
                        "version": "1.0.0",
                        "informationalVersion": ""
                      },
                      {
                        "name": "Sample",
                        "version": "1.0.0",
                        "informationalVersion": ""
                      },
                      {
                        "name": "TPDM",
                        "version": "1.1.0",
                        "informationalVersion": ""
                      }
                    ],
                    "urls": {
                      "dependencies": "{BASE_URL}/metadata/dependencies",
                      "openApiMetadata": "{BASE_URL}/metadata/specifications",
                      "oauth": "{OAUTH_URL}",
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
                      },
                      {
                          "name": "E2E-Test-School-IncludeOnly",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-IncludeOnly/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-ExcludeOnly",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-ExcludeOnly/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-IncludeAll",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-IncludeAll/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-GradeLevelFilter",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-GradeLevelFilter/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-GradeLevelExcludeFilter",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-GradeLevelExcludeFilter/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-IncludeOnly-Alt",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-IncludeOnly-Alt/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Extension-IncludeOnly",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Extension-IncludeOnly/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Extension-ExcludeOnly",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Extension-ExcludeOnly/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-IncludeOnly-NoExtensionRule",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-IncludeOnly-NoExtensionRule/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-ExcludeOnly-NoExtensionRule",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-ExcludeOnly-NoExtensionRule/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-IncludeOnly",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-IncludeOnly/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-ExcludeOnly",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-ExcludeOnly/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-GradeLevelFilter",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-GradeLevelFilter/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-ExcludeRequired",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-ExcludeRequired/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-ExcludeRequiredCollection",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-ExcludeRequiredCollection/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-IncludeOnlyMissingRequired",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-IncludeOnlyMissingRequired/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-IncludeAll",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-IncludeAll/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-RequiredCollectionWithRule",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-RequiredCollectionWithRule/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-AddressExcludeNameOfCounty",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-AddressExcludeNameOfCounty/resources-spec.json",
                          "prefix": "Profiles"
                      },
                      {
                          "name": "E2E-Test-School-Write-GradeLevelFilterPreserve",
                          "endpointUri": "{BASE_URL}/metadata/specifications/profiles/E2E-Test-School-Write-GradeLevelFilterPreserve/resources-spec.json",
                          "prefix": "Profiles"
                      }
                  ]
                  """

        @API-065
        Scenario: 04 GET /metadata/dependencies returns the dependency order for loading documents
             When a GET request is made to "/metadata/dependencies"
             Then it should respond with 200
              And there is a JSON file in the response body with a list of dependencies
