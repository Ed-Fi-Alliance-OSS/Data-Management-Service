Feature: A read that asks for a snapshot it cannot be served from returns Snapshot Not Found.

        # Two deployment facts produce this response, and a client must not be able to tell them
        # apart: no snapshot is configured for the data store, and a configured snapshot cannot be
        # reached. Each scenario below arranges one of them and asserts the identical body, so a drift
        # at either producer would fail here.
        #
        # The arrangement is selected by scenario tag (AuthorizationDataProvider):
        #   - no derivative tag                        -> the data store has no derivatives at all.
        #   - @derivative-routing-unreachable-snapshot -> a Snapshot derivative naming a database that
        #                                                 does not exist, and no read replica, so a
        #                                                 header-less read is served by the primary.
        #
        # This feature deliberately carries no feature-level derivative tag: the "no snapshot
        # configured" arrangement is expressed by the absence of one. CI selection is separate from
        # arrangement, so every scenario carries a shard tag regardless of which one it arranges.
        #
        # Assertions name values these scenarios write, never collection counts, because the E2E
        # database is shared with every other feature.

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"

        @e2e-ci-shard-1
        Scenario: 01 A read asking for a snapshot that is not configured returns Snapshot Not Found
            # The same read without the header succeeds, so the 404 is about the snapshot rather than
            # about the resource or the data store.
             When a GET request is made to "/ed-fi/contentClassDescriptors"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/contentClassDescriptors" with header "Use-Snapshot" value "true"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Snapshot not found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        @e2e-ci-shard-1
        @derivative-routing-unreachable-snapshot
        @MssqlRepresentative
        Scenario: 02 A read asking for a snapshot that cannot be reached returns the same response
            # The snapshot is genuinely configured here and names a database that does not exist, so
            # the request fails inside connection acquisition rather than at selection. The body is
            # identical to scenario 01's: which of the two causes produced it is a deployment fact the
            # client has no business distinguishing.
             When a POST request is made to "/ed-fi/contentClassDescriptors" with
                  """
                  {
                      "codeValue": "SnapshotUnreachable-Read",
                      "shortDescription": "Written to the primary",
                      "description": "Written to the primary",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/ed-fi/contentClassDescriptors" with header "Use-Snapshot" value "true"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Snapshot not found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """
            # The snapshot-requesting read never falls back to the primary, and its failure leaves the
            # primary path alone: the same read without the header still returns what was written.
             When a GET request is made to "/ed-fi/contentClassDescriptors"
             Then it should respond with 200
              And the response body should contain "SnapshotUnreachable-Read"
