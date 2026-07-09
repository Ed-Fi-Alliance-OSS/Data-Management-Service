Feature: ETag validations

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        @API-260
        @e2e-ci-shard-1
        Scenario: 01 Ensure that clients can retrieve an ETag in the response header
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201
              And the ETag is in the response header
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "111111",
                    "birthDate": "2014-08-14",
                    "firstName": "Russella",
                    "lastSurname": "Mayers",
                    "_etag": "{etag}"
                  }
                  """
        @e2e-ci-shard-1
        Scenario: 02 Ensure that clients can pass an IfMatch in the request header
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-match "{IfMatch}" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 03 Ensure that clients can pass an IfMatch in the request header and ignore _etag in request body
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-match "{IfMatch}" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga",
                      "_etag": "{etag}"
                  }
                  """
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 04 Ensure that clients cannot pass a different If-Match in the request header
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-match "0000000000" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mulligan"
                  }
                  """
             Then it should respond with 412
              And the response body is
                  """
                  {
                      "detail": "The item has been modified by another user.",
                      "type": "urn:ed-fi:api:optimistic-lock-failed",
                      "title": "Optimistic Lock Failed",
                      "status": 412,
                      "validationErrors": {},
                      "errors": [
                        "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                      ]
                  }
                  """
        @e2e-ci-shard-1
        Scenario: 05 Ensure that clients cannot pass a different ETag in the If-Match header to delete a resource
            Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             When a DELETE if-match "0000000000" request is made to "/ed-fi/students/{id}"
             Then it should respond with 412
              And the response body is
                  """
                  {
                      "detail": "The item has been modified by another user.",
                      "type": "urn:ed-fi:api:optimistic-lock-failed",
                      "title": "Optimistic Lock Failed",
                      "status": 412,
                      "validationErrors": {},
                      "errors": [
                          "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                      ]
                  }
                  """
        @e2e-ci-shard-1
        Scenario: 06 Ensure that clients can pass an ETag to delete a resource
            Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             When a DELETE if-match "{IfMatch}" request is made to "/ed-fi/students/{id}"
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 07 Ensure that clients can pass a wildcard If-Match on a PUT to an existing resource
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-match "*" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 08 Ensure that clients can pass a wildcard If-Match to delete an existing resource
            Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             When a DELETE if-match "*" request is made to "/ed-fi/students/{id}"
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 09 Ensure that a wildcard If-Match on a PUT to a non-existent resource returns 412
             # The id value is a non-existing resource
             When a PUT if-match "*" request is made to "/ed-fi/students/00000000-0000-4000-a000-000000000000" with
                  """
                  {
                      "id": "00000000-0000-4000-a000-000000000000",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 412
              And the response body is
                  """
                  {
                      "detail": "The If-Match precondition failed because the resource does not exist.",
                      "type": "urn:ed-fi:api:optimistic-lock-failed",
                      "title": "Optimistic Lock Failed",
                      "status": 412,
                      "validationErrors": {},
                      "errors": [
                          "The 'If-Match' request header requires a current representation of the resource, but none exists. Do not retry with If-Match; create the resource first, or omit If-Match."
                      ]
                  }
                  """
        @e2e-ci-shard-1
        Scenario: 10 Ensure that a wildcard If-Match on a DELETE to a non-existent resource returns 412
             # The id value is a non-existing resource
             When a DELETE if-match "*" request is made to "/ed-fi/students/00000000-0000-4000-a000-000000000000"
             Then it should respond with 412
              And the response body is
                  """
                  {
                      "detail": "The If-Match precondition failed because the resource does not exist.",
                      "type": "urn:ed-fi:api:optimistic-lock-failed",
                      "title": "Optimistic Lock Failed",
                      "status": 412,
                      "validationErrors": {},
                      "errors": [
                          "The 'If-Match' request header requires a current representation of the resource, but none exists. Do not retry with If-Match; create the resource first, or omit If-Match."
                      ]
                  }
                  """
        @e2e-ci-shard-1
        Scenario: 11 Ensure that clients receive a 304 Not Modified on a GET when If-None-Match matches the current ETag
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a GET if-none-match "{IfMatch}" request is made to "/ed-fi/students/{id}"
             Then it should respond with 304
        @e2e-ci-shard-1
        Scenario: 12 Ensure that a wildcard If-None-Match on a GET to an existing resource returns 304
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a GET if-none-match "*" request is made to "/ed-fi/students/{id}"
             Then it should respond with 304
        @e2e-ci-shard-1
        Scenario: 13 Ensure that clients receive a 200 on a GET when If-None-Match does not match the current ETag
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a GET if-none-match "0000000000" request is made to "/ed-fi/students/{id}"
             Then it should respond with 200
        @e2e-ci-shard-1
        Scenario: 14 Ensure that clients can pass a wildcard If-None-Match on a POST that creates a new resource
             When a POST request is made to "/ed-fi/students" with header "If-None-Match" value "*"
                  """
                  {
                     "studentUniqueId": "111115",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201
              And the ETag is in the response header
        @e2e-ci-shard-1
        Scenario: 15 Ensure that a wildcard If-None-Match on a POST to an already-existing resource returns 412
             Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             When a POST request is made to "/ed-fi/students" with header "If-None-Match" value "*"
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mulligan"
                  }
                  """
             Then it should respond with 412
        @e2e-ci-shard-1
        Scenario: 16 Ensure that a wildcard If-None-Match on a PUT to an existing resource returns 412
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-none-match "*" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
             Then it should respond with 412
        @e2e-ci-shard-1
        Scenario: 17 Ensure that a quoted If-Match (as emitted) is accepted on PUT
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the quoted ETag is in the response header
             When a PUT if-match "{IfMatchQuoted}" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 18 Ensure that a quoted If-Match (as emitted) is accepted on DELETE
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the quoted ETag is in the response header
             When a DELETE if-match "{IfMatchQuoted}" request is made to "/ed-fi/students/{id}"
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 19 Ensure the served ETag conforms to the DMS-1252 format and matches the body _etag
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the quoted ETag is in the response header
              And the ETag value matches the pattern "^\d+-[0-9a-f]{8}\.j\.(_|[0-9a-f]{8})\.[ln]$"
              And the ETag is in the response header
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "111111",
                    "birthDate": "2014-08-14",
                    "firstName": "Russella",
                    "lastSurname": "Mayers",
                    "_etag": "{etag}"
                  }
                  """
        @e2e-ci-shard-1
        Scenario: 20 Ensure a child-collection-only update advances the ETag and invalidates a stale If-Match
            Given the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/AddressTypeDescriptor#Mailing                  |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                 |
              And the system has these "students"
                  | studentUniqueId | birthDate  | firstName | lastSurname |
                  | "604824"        | 2010-01-13 | Traci     | Mathews     |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
             When a POST request is made to "/ed-fi/studentEducationOrganizationAssociations" with
                  """
                  {
                      "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                      "studentReference": { "studentUniqueId": "604824" },
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                              "city": "Grand Bend",
                              "postalCode": "78834",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "980 Green New Boulevard",
                              "nameOfCounty": "WILLISTON",
                              "periods": []
                          }
                      ]
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is stored in request variable "originalEtag"
            # A child-collection-only change (address city) must still advance the served ETag: the ADR
            # restored the post-write ContentVersion read specifically so the response reflects the
            # trigger-stamped version rather than a stale one.
             When a PUT request is made to "/ed-fi/studentEducationOrganizationAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                      "studentReference": { "studentUniqueId": "604824" },
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                              "city": "Springfield",
                              "postalCode": "78834",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "980 Green New Boulevard",
                              "nameOfCounty": "WILLISTON",
                              "periods": []
                          }
                      ]
                  }
                  """
             Then it should respond with 204
              And the ETag differs from request variable "originalEtag"
            # The original ETag is now stale, so replaying it as If-Match must be rejected.
             When a PUT if-match "{originalEtag}" request is made to "/ed-fi/studentEducationOrganizationAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                      "studentReference": { "studentUniqueId": "604824" },
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                              "city": "Lakeview",
                              "postalCode": "78834",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "980 Green New Boulevard",
                              "nameOfCounty": "WILLISTON",
                              "periods": []
                          }
                      ]
                  }
                  """
             Then it should respond with 412
            # The current ETag still satisfies If-Match.
             When a PUT if-match "{IfMatch}" request is made to "/ed-fi/studentEducationOrganizationAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                      "studentReference": { "studentUniqueId": "604824" },
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                              "city": "Lakeview",
                              "postalCode": "78834",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "980 Green New Boulevard",
                              "nameOfCounty": "WILLISTON",
                              "periods": []
                          }
                      ]
                  }
                  """
             Then it should respond with 204
        @e2e-ci-shard-1
        Scenario: 21 Ensure that a wildcard If-None-Match on a GET to a non-existent resource returns 404
             # The id value is a non-existing resource
             When a GET if-none-match "*" request is made to "/ed-fi/students/00000000-0000-4000-a000-000000000000"
             Then it should respond with 404
        @e2e-ci-shard-1
        Scenario: 22 Ensure that a specific If-None-Match on a GET to a non-existent resource returns 404
             # The id value is a non-existing resource
             When a GET if-none-match "some-etag" request is made to "/ed-fi/students/00000000-0000-4000-a000-000000000000"
             Then it should respond with 404
