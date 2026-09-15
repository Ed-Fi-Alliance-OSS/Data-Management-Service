Feature: Served OpenAPI documents advertise the snapshot contract
              As an API client deciding whether an extraction can be isolated
              I want the served documents to describe the Use-Snapshot header and its responses
        So that I can read the contract from the API rather than from release notes

        # These run against a stack whose schema comes from SCHEMA_PACKAGES, so they exercise the
        # file-based intake path end to end: the packages are downloaded and staged at deployment
        # time, assembled by DMS, and served over HTTP.

        @e2e-ci-shard-4
        @MssqlRepresentative
        Scenario: 01 The resources document advertises the snapshot contract
             When a GET request is made to "/metadata/specifications/resources-spec.json"
             Then it should respond with 200
              And the served OpenAPI document declares boolean header parameter "Use-Snapshot" defaulting to false
              And the served OpenAPI document declares response "SnapshotNotFound" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with header "Allow" example "GET"
              And the served OpenAPI operation "get" on path "/ed-fi/schools" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/{id}" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/deletes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/keyChanges" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/{id}" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/deletes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/keyChanges" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "post" on path "/ed-fi/schools" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "put" on path "/ed-fi/schools/{id}" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "delete" on path "/ed-fi/schools/{id}" answers "405" with response "SnapshotMethodNotAllowed"
              And every local reference in the served OpenAPI document resolves

        @e2e-ci-shard-4
        Scenario: 02 The descriptors document advertises the snapshot contract
             When a GET request is made to "/metadata/specifications/descriptors-spec.json"
             Then it should respond with 200
              And the served OpenAPI document declares boolean header parameter "Use-Snapshot" defaulting to false
              And the served OpenAPI document declares response "SnapshotNotFound" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with header "Allow" example "GET"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/{id}" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/deletes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/keyChanges" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/{id}" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/deletes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/keyChanges" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "post" on path "/ed-fi/absenceEventCategoryDescriptors" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "delete" on path "/ed-fi/absenceEventCategoryDescriptors/{id}" answers "405" with response "SnapshotMethodNotAllowed"
              And every local reference in the served OpenAPI document resolves

        # The standalone Change Queries document ships with its own components block. It is served
        # independently, so it cannot borrow the parameter or the response from the resources document.
        @e2e-ci-shard-4
        Scenario: 03 The Change Queries document advertises the snapshot contract on its own
             When a GET request is made to "/metadata/changequeries/v1/swagger.json"
             Then it should respond with 200
              And the served OpenAPI document declares boolean header parameter "Use-Snapshot" defaulting to false
              And the served OpenAPI document declares response "SnapshotNotFound" with content type "application/problem+json"
              And the served OpenAPI operation "get" on path "/availableChangeVersions" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/availableChangeVersions" answers "404" with response "SnapshotNotFound"
              And every local reference in the served OpenAPI document resolves

        # Extension resources reach the served document through fragment merging rather than through the
        # base document, so their coverage is a separate question from core's.
        @e2e-ci-shard-4
        Scenario: 04 Extension resources advertise the snapshot contract
             When a GET request is made to "/metadata/specifications/resources-spec.json"
             Then it should respond with 200
              And the served OpenAPI operation "get" on path "/tpdm/candidates" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/tpdm/candidates/deletes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/tpdm/candidates" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/tpdm/candidates/deletes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "post" on path "/tpdm/candidates" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "get" on path "/sample/busRoutes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/sample/busRoutes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/homograph/contacts" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/homograph/contacts" answers "404" with response "SnapshotNotFound"

        # Data Standard 6.1. Tagged by standard version with no shard tag, the convention the other
        # 6.1 scenarios use, so the DS 5.2 shard lanes skip them and the 6.1 lane runs them against a
        # 6.1 stack. This is the half of the supported package set the bundled path never sees: 6.1
        # reaches a running DMS only through SCHEMA_PACKAGES, as Core plus Sample plus Homograph.
        @StandardVersion-6_1
        Scenario: 05 The Data Standard 6.1 resources document advertises the snapshot contract
             When a GET request is made to "/metadata/specifications/resources-spec.json"
             Then it should respond with 200
              And the served OpenAPI document declares boolean header parameter "Use-Snapshot" defaulting to false
              And the served OpenAPI document declares response "SnapshotNotFound" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with header "Allow" example "GET"
              And the served OpenAPI operation "get" on path "/ed-fi/schools" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/{id}" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/deletes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/keyChanges" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/schools" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/{id}" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/deletes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/keyChanges" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "post" on path "/ed-fi/schools" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "put" on path "/ed-fi/schools/{id}" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "delete" on path "/ed-fi/schools/{id}" answers "405" with response "SnapshotMethodNotAllowed"
              And every local reference in the served OpenAPI document resolves

        # Data Standard 6.1 folds TPDM into core, so candidates is served from the core namespace
        # rather than from a separate extension package. Sample and Homograph remain extensions.
        @StandardVersion-6_1
        Scenario: 06 Data Standard 6.1 folded and extension resources advertise the snapshot contract
             When a GET request is made to "/metadata/specifications/resources-spec.json"
             Then it should respond with 200
              And the served OpenAPI operation "get" on path "/ed-fi/candidates" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/candidates/deletes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/candidates" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/candidates/deletes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "post" on path "/ed-fi/candidates" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "get" on path "/sample/busRoutes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/sample/busRoutes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/homograph/contacts" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/homograph/contacts" answers "404" with response "SnapshotNotFound"

        @StandardVersion-6_1
        Scenario: 07 The Data Standard 6.1 descriptors document advertises the snapshot contract
             When a GET request is made to "/metadata/specifications/descriptors-spec.json"
             Then it should respond with 200
              And the served OpenAPI document declares boolean header parameter "Use-Snapshot" defaulting to false
              And the served OpenAPI document declares response "SnapshotNotFound" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with content type "application/problem+json"
              And the served OpenAPI document declares response "SnapshotMethodNotAllowed" with header "Allow" example "GET"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/{id}" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/deletes" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/keyChanges" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/{id}" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/deletes" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "get" on path "/ed-fi/absenceEventCategoryDescriptors/keyChanges" answers "404" with response "SnapshotNotFound"
              And the served OpenAPI operation "post" on path "/ed-fi/absenceEventCategoryDescriptors" answers "405" with response "SnapshotMethodNotAllowed"
              And the served OpenAPI operation "delete" on path "/ed-fi/absenceEventCategoryDescriptors/{id}" answers "405" with response "SnapshotMethodNotAllowed"
              And every local reference in the served OpenAPI document resolves

        @StandardVersion-6_1
        Scenario: 08 The Data Standard 6.1 Change Queries document advertises the snapshot contract on its own
             When a GET request is made to "/metadata/changequeries/v1/swagger.json"
             Then it should respond with 200
              And the served OpenAPI document declares boolean header parameter "Use-Snapshot" defaulting to false
              And the served OpenAPI document declares response "SnapshotNotFound" with content type "application/problem+json"
              And the served OpenAPI operation "get" on path "/availableChangeVersions" references parameter "Use-Snapshot"
              And the served OpenAPI operation "get" on path "/availableChangeVersions" answers "404" with response "SnapshotNotFound"
              And every local reference in the served OpenAPI document resolves
