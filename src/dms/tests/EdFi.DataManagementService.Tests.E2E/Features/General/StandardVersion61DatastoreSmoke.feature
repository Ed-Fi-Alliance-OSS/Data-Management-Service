Feature: Data Standard 6.1 relational datastore smoke

        # Proves that a public data-plane request traverses the registered datastore on a Data Standard
        # 6.1 stack, complementing the metadata/XSD/Discovery @StandardVersion-6_1 scenarios that never
        # touch the datastore. It runs only in the version-coupled DS 6.1 lanes (run-e2e-tests-ds61 and
        # run-e2e-tests-mssql-ds61 filter the @StandardVersion-6_1 category against a 6.1 stack) and
        # carries no shard tag, so the DS 5.2 shard lanes never pick it up. A descriptor is used because
        # it is a single-resource write with no reference chain, keeping this a small representative
        # smoke rather than the broad provider write matrix owned by neighboring work.

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"

        @StandardVersion-6_1
        Scenario: 01 A descriptor round-trips through the datastore (DS 6.1)
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
                      "codeValue": "DS61 Datastore Smoke",
                      "description": "DS61 Datastore Smoke",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "DS61 Datastore Smoke"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/ed-fi/absenceEventCategoryDescriptors/{id}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "codeValue": "DS61 Datastore Smoke",
                      "description": "DS61 Datastore Smoke",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "DS61 Datastore Smoke"
                  }
                  """
