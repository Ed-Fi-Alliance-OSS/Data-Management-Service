Feature: ClaimsManagement endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Verify hybrid mode assembles fragments to match authoritative composition
             When a GET request is made to "/management/current-claims"
             Then it should respond with 200
              And the response headers include "X-Reload-Id"
              And the response body matches the authoritative composition structure
              And the response contains claim set "E2E-NameSpaceBasedClaimSet"
              And the response contains claim set "E2E-NoFurtherAuthRequiredClaimSet"
              And the response contains claim set "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
              And the response contains extension claims "HomographExtension"
              And the response contains extension claims "SampleExtension"

        Scenario: 02 Upload new claims replaces hybrid mode composition
            Given the initial reload ID is captured
             When a POST request is made to "/management/upload-claims" with
                  """
                  {
                      "claims": {
                          "claimSets": [{"claimSetName": "TestUploadSet", "isSystemReserved": false}],
                          "claimsHierarchy": [
                              {
                                  "name": "http://ed-fi.org/identity/claims/test",
                                  "claimSets": [
                                      {
                                          "name": "TestUploadSet",
                                          "actions": [{"name": "Read"}]
                                      }
                                  ]
                              }
                          ]
                      }
                  }
                  """
             Then it should respond with 200
              And the response body contains a new reload ID
             When a GET request is made to "/management/current-claims"
             Then it should respond with 200
              And the response headers include a different "X-Reload-Id"
              And the response body contains only the uploaded claims
              And the response does not contain claim set "E2E-NameSpaceBasedClaimSet"

        Scenario: 03 Reload restores hybrid mode fragment composition
            Given claims have been uploaded
             When a POST request is made to "/management/reload-claims"
             Then it should respond with 200
              And the response body contains success=true
              And the response body contains a new reload ID
             When a GET request is made to "/management/current-claims"
             Then it should respond with 200
              And the response body matches the authoritative composition structure
              And the uploaded claims are no longer present

        Scenario: 04 Verify composed claims have no empty arrays
             When a GET request is made to "/management/current-claims"
             Then it should respond with 200
              And the response body contains no empty arrays
              And all collection properties are either null or have items

        Scenario: 05 Upload invalid claims returns error
             When a POST request is made to "/management/upload-claims" with
                  """
                  {
                      "claims": {
                          "claimSets": [{"invalidProperty": "test"}],
                          "claimsHierarchy": []
                      }
                  }
                  """
             Then it should respond with 400
              And the response body contains validation errors

