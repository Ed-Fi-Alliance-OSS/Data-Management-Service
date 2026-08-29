@derivative-routing
Feature: Reads are served by the snapshot or read replica the request selects.

        # This suite creates its own data store in CMS for each client-credential setup, and attaches
        # both derivatives to it at that moment (AuthorizationDataProvider):
        #   - Snapshot     -> a second database provisioned with the same DDL and left empty.
        #   - ReadReplica  -> the data store's own database.
        #
        # That arrangement is what makes routing observable on a suite with a single data store. DMS
        # never writes to a derivative, so the snapshot stays empty: anything these scenarios write is
        # visible to a plain read and absent from a snapshot-requesting read, which is only possible if
        # the two reads were served by different physical databases. The read replica deliberately
        # points at the primary database, so every other scenario in this suite keeps reading the data
        # it wrote while a read replica is genuinely configured - which is what snapshot precedence is
        # asserted against here.
        #
        # Assertions are on values these scenarios write, never on collection counts, because the E2E
        # database is shared with every other feature.

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"

        @e2e-ci-shard-1
        Scenario: 01 GET-many is served by the read replica, and Use-Snapshot selects the snapshot instead
             When a POST request is made to "/ed-fi/contentClassDescriptors" with
                  """
                  {
                      "codeValue": "DerivRouting-GetMany",
                      "shortDescription": "Written to the primary",
                      "description": "Written to the primary",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with 201
            # Replica-eligible read with no header: served by the configured read replica.
             When a GET request is made to "/ed-fi/contentClassDescriptors"
             Then it should respond with 200
              And the response body should contain "DerivRouting-GetMany"
            # The same read asking for a snapshot is served by the snapshot, overriding the configured
            # read replica. This is the snapshot-precedence case.
             When a GET request is made to "/ed-fi/contentClassDescriptors" with header "Use-Snapshot" value "true"
             Then it should respond with 200
              And the response body should not contain "DerivRouting-GetMany"

        @e2e-ci-shard-1
        Scenario: 02 GET-by-id is served by the selected target
             When a POST request is made to "/ed-fi/contentClassDescriptors" with
                  """
                  {
                      "codeValue": "DerivRouting-ById",
                      "shortDescription": "Written to the primary",
                      "description": "Written to the primary",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/ed-fi/contentClassDescriptors/{id}"
             Then it should respond with 200
              And the response body should contain "DerivRouting-ById"
            # The snapshot never received this document, so the same id is absent there.
             When a GET request is made to "/ed-fi/contentClassDescriptors/{id}" with header "Use-Snapshot" value "true"
             Then it should respond with 404

        @e2e-ci-shard-1
        Scenario: 03 The deletes surface is served by the selected target
             When a POST request is made to "/ed-fi/contentClassDescriptors" with
                  """
                  {
                      "codeValue": "DerivRouting-Deletes",
                      "shortDescription": "Deleted from the primary",
                      "description": "Deleted from the primary",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/ed-fi/contentClassDescriptors/{id}"
             Then it should respond with 204
            # The tracked-change row carries the deleted document's key values, so the assertion names
            # the value this scenario wrote rather than a generated id.
             When a GET request is made to "/ed-fi/contentClassDescriptors/deletes"
             Then it should respond with 200
              And the response body should contain "DerivRouting-Deletes"
             When a GET request is made to "/ed-fi/contentClassDescriptors/deletes" with header "Use-Snapshot" value "true"
             Then it should respond with 200
              And the response body should not contain "DerivRouting-Deletes"

        @e2e-ci-shard-1
        Scenario: 04 The keyChanges surface is served by the selected target
            # A keyChanges row needs a real identity update, and ClassPeriod allows one. The school
            # write needs education-organization scope, which the Background's namespace-only claim set
            # does not carry, so this scenario authorizes as the SIS Vendor instead.
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930190101,
                    "nameOfInstitution": "Derivative Routing KeyChange School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "DerivRouting-KeyChange-A",
                    "schoolReference": { "schoolId": 930190101 }
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/ed-fi/classPeriods/{id}" with
                  """
                  {
                    "id": "{id}",
                    "classPeriodName": "DerivRouting-KeyChange-B",
                    "schoolReference": { "schoolId": 930190101 }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/classPeriods/keyChanges"
             Then it should respond with 200
              And the response body should contain "DerivRouting-KeyChange-B"
             When a GET request is made to "/ed-fi/classPeriods/keyChanges" with header "Use-Snapshot" value "true"
             Then it should respond with 200
              And the response body should not contain "DerivRouting-KeyChange"

        @e2e-ci-shard-1
        Scenario: 05 availableChangeVersions reports the selected target, and the snapshot overrides the replica
            # Each comparison is before/after against the same selected target, so neither depends on
            # the two databases reporting different absolute values.
            #
            # The change is a DELETE rather than the POST that precedes it. A POST of an already-present
            # descriptor is an idempotent upsert and does not always advance the change version, so a
            # re-run against a database this feature has already touched would see an unchanged value
            # and fail for a reason that has nothing to do with routing. A delete always advances it.
             When a POST request is made to "/ed-fi/contentClassDescriptors" with
                  """
                  {
                      "codeValue": "DerivRouting-ChangeVersion",
                      "shortDescription": "Deleted from the primary",
                      "description": "Deleted from the primary",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "plainBefore"
             When a GET request is made to "/changeQueries/v1/availableChangeVersions" with header "Use-Snapshot" value "true"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "snapshotBefore"
             When a DELETE request is made to "/ed-fi/contentClassDescriptors/{id}"
             Then it should respond with 204
            # The plain read is answered by the read replica, which is the database that received the
            # delete, so its reported change version moved.
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" should not equal request variable "plainBefore"
            # The snapshot-requesting read is answered by the snapshot, which received nothing, so its
            # reported change version is unchanged. Snapshot selection overrode the configured replica.
             When a GET request is made to "/changeQueries/v1/availableChangeVersions" with header "Use-Snapshot" value "true"
             Then it should respond with 200
              And the response body path "newestChangeVersion" should equal request variable "snapshotBefore"
