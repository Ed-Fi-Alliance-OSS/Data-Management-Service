Feature: Profile OpenAPI Specification Filtering
              As an API client with profiles assigned
              I want to receive correctly filtered OpenAPI specifications for my assigned profiles
    So that I can understand what operations and data are available to me

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"

        Scenario: 01 Profile OpenAPI spec includes only covered resources
             When a GET request is made to "/metadata/specifications/profiles/E2E-Test-School-IncludeOnly/resources-spec.json"
             Then the profile response status is 200
              And the OpenAPI spec should contain path "/ed-fi/schools"
              And the OpenAPI spec should not contain path "/ed-fi/students"
              And the OpenAPI spec should contain schema "edFi_school_readable"
              And the OpenAPI spec should not contain schema "edFi_school"
              And the OpenAPI spec should contain tag "Schools"
              And the OpenAPI spec should not contain tag "Students"

        Scenario: 02 Profile OpenAPI spec includes readable and writable schemas
             When a GET request is made to "/metadata/specifications/profiles/E2E-Test-School-IncludeOnly/resources-spec.json"
             Then the profile response status is 200
              And the OpenAPI spec should contain schema "edFi_school_readable"
              And the OpenAPI spec should contain schema "edFi_school_writable"

        Scenario: 03 Profile OpenAPI spec filters operations correctly
             When a GET request is made to "/metadata/specifications/profiles/E2E-Test-School-IncludeOnly/resources-spec.json"
             Then the profile response status is 200
              And the OpenAPI spec path "/ed-fi/schools" should have operation "get"
              And the OpenAPI spec path "/ed-fi/schools" should have operation "post"
              And the OpenAPI spec path "/ed-fi/schools/{id}" should have operation "get"
              And the OpenAPI spec path "/ed-fi/schools/{id}" should have operation "put"
              And the OpenAPI spec path "/ed-fi/schools/{id}" should have operation "delete"

        Scenario: 04 Non-existent profile returns 404
             When a GET request is made to "/metadata/specifications/profiles/NonExistentProfile/resources-spec.json"
             Then the profile response status is 404

