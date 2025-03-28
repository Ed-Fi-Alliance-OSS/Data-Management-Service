Feature: Tpdm extension resources and descriptors

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org, uri://tpdm.ed-fi.org"

    Rule: Descriptors
        Scenario: 01 Ensure clients can create a descriptor
             When a POST request is made to "/tpdm/accreditationStatusDescriptors" with
                  """
                  {
                      "codeValue": "Accredited",
                      "description": "Accredited",
                      "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                      "shortDescription": "Accredited",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/tpdm/accreditationStatusDescriptors/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "codeValue": "Accredited",
                      "description": "Accredited",
                      "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                      "shortDescription": "Accredited",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14"
                  }
                  """
        Scenario: 02 Ensure clients can update a descriptor
             When a POST request is made to "/tpdm/accreditationStatusDescriptors" with
                  """
                  {
                      "codeValue": "Accredited1",
                      "description": "Accredited1",
                      "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                      "shortDescription": "Accredited1",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14"
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/tpdm/accreditationStatusDescriptors/{id}" with
                  """
                  {
                      "id": "{id}",
                      "codeValue": "Accredited1",
                      "description": "Accredited Edited",
                      "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                      "shortDescription": "Accredited1",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "codeValue": "Accredited1",
                      "description": "Accredited Edited",
                      "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                      "shortDescription": "Accredited1",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14"
                  }
                  """
        @addwait
        Scenario: 03 Ensure clients can retrieve a descriptor by requesting through a valid codeValue
             Given a POST request is made to "/tpdm/accreditationStatusDescriptors" with
                  """
                  {
                      "codeValue": "Not Accredited",
                      "description": "Not Accredited",
                      "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                      "shortDescription": "Not Accredited",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/tpdm/accreditationStatusDescriptors?codeValue=Not+Accredited"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "codeValue": "Not Accredited",
                          "description": "Not Accredited",
                          "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                          "shortDescription": "Not Accredited",
                          "effectiveBeginDate": "2024-05-14",
                          "effectiveEndDate": "2024-05-14"
                      }
                  ]
                  """

        Scenario: 04 Ensure clients can delete a descriptor
             Given a POST request is made to "/tpdm/accreditationStatusDescriptors" with
                  """
                  {
                      "codeValue": "Delete Accredited",
                      "description": "Delete Accredited",
                      "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                      "shortDescription": "Delete Accredited",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/tpdm/accreditationStatusDescriptors/{id}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                          "id": "{id}",
                          "codeValue": "Delete Accredited",
                          "description": "Delete Accredited",
                          "namespace": "uri://tpdm.ed-fi.org/AccreditationStatusDescriptor",
                          "shortDescription": "Delete Accredited",
                          "effectiveBeginDate": "2024-05-14",
                          "effectiveEndDate": "2024-05-14"
                      }
                  """
             When a DELETE request is made to "/tpdm/accreditationStatusDescriptors/{id}"
             Then it should respond with 204

    Rule: Resources

        Scenario: 05 Ensure clients can create a resource
             When a POST request is made to "/tpdm/candidates" with
                  """
                  {
                      "candidateIdentifier": "10000412",
                      "birthDate": "2005-10-03",
                      "firstName": "Bryce",
                      "lastSurname": "Beatty"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/tpdm/candidates/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "candidateIdentifier": "10000412",
                      "birthDate": "2005-10-03",
                      "firstName": "Bryce",
                      "lastSurname": "Beatty"
                  }
                  """

        Scenario: 06 Ensure clients can update a resource
             When a POST request is made to "/tpdm/candidates" with
                  """
                  {
                      "candidateIdentifier": "10000413",
                      "birthDate": "2005-10-03",
                      "firstName": "ToUpdateFN",
                      "lastSurname": "ToUpdateLN"
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/tpdm/candidates/{id}" with
                  """
                  {
                      "id": "{id}",
                      "candidateIdentifier": "10000413",
                      "birthDate": "2005-10-03",
                      "firstName": "FNUpdated",
                      "lastSurname": "LNUpdated"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "candidateIdentifier": "10000413",
                      "birthDate": "2005-10-03",
                      "firstName": "FNUpdated",
                      "lastSurname": "LNUpdated"
                  }
                  """
        @addwait
        Scenario: 07 Ensure clients can query a resource
             Given a POST request is made to "/tpdm/candidates" with
                  """
                  {
                      "candidateIdentifier": "10000414",
                      "birthDate": "2005-10-03",
                      "firstName": "ToQueryFN",
                      "lastSurname": "ToQueryLN"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/tpdm/candidates?firstName=ToQueryFN"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "candidateIdentifier": "10000414",
                          "birthDate": "2005-10-03",
                          "firstName": "ToQueryFN",
                          "lastSurname": "ToQueryLN"
                      }
                  ]
                  """

        Scenario: 08 Ensure clients can delete a resource
             Given a POST request is made to "/tpdm/candidates" with
                  """
                  {
                      "candidateIdentifier": "10000415",
                      "birthDate": "2005-10-03",
                      "firstName": "ToDeleteFN",
                      "lastSurname": "ToDeleteLN"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/tpdm/candidates/{id}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                          "id": "{id}",
                          "candidateIdentifier": "10000415",
                          "birthDate": "2005-10-03",
                          "firstName": "ToDeleteFN",
                          "lastSurname": "ToDeleteLN"
                      }
                  """
             When a DELETE request is made to "/tpdm/candidates/{id}"
             Then it should respond with 204
