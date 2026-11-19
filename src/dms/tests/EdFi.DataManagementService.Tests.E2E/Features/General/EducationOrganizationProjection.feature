@EducationOrganizationProjection
Feature: Education organization projection through the URLs Discovery advertises
    A Configuration Service job reads the projection the way its reader does: it reads the Discovery
    document, gets a token from the URL Discovery advertises as urls.oauth with HTTP Basic client
    credentials and no scope, and reads the URL Discovery advertises as
    urls.educationOrganizationProjection. Only a credential whose claim set grants the projection claim
    reads it.

        @e2e-ci-shard-2 @MssqlRepresentative
        Scenario: 01 A projection-only credential reads the projection through the discovered URLs and a resource-only credential is refused
            Given a resource-only application on claim set "EdFiSandbox" with its own data store
              And a projection-only application whose claim set grants only Read on the education organization projection
             When the Discovery document is read
              And the "projection-only" application reads the projection of the resource-only application's data store through the discovered URLs
             Then the projection read responds with 200 and a projection page of that data store
             When the "resource-only" application reads the projection of the resource-only application's data store through the discovered URLs
             Then the projection read responds with 403 and no items
