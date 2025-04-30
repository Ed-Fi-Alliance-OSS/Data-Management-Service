Feature: Homograph extension resources

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"

    Rule: Homograph Extension Resources

        Scenario: 01 Ensure clients can create/update/get/delete a school in homograph extension
      # POST request to create a school
             When a POST request is made to "/homograph/schools/" with
                  """
                  {
                      "schoolName": "UT Austin"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/schools/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolName": "UT Austin"
                  }
                  """

      # PUT request to update the school
             When a PUT request is made to "/homograph/schools/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolName": "UT Austin",
                      "address": {
                        "city": "Austin"
                      }
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolName": "UT Austin",
                      "address": {
                        "city": "Austin"
                      }
                  }
                  """

      # DELETE request to delete the school
             When a DELETE request is made to "/homograph/schools/{id}"
             Then it should respond with 204
      # Optionally, verify the resource is deleted
             When a GET request is made to "/homograph/schools/{id}"
             Then it should respond with 404

        Scenario: 02 Ensure clients can create/update/get/delete a student in homograph extension
      # POST request to create a schoolYearType
            Given a POST request is made to "/homograph/schoolYearTypes/" with
                  """
                  {
                      "schoolYear": "2025",
                      "currentSchoolYear": true,
                      "schoolYearDescription": "2024-2025"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/schoolYearTypes/{id}"
                  }
                  """

      # POST request to create a name (firstName, lastSurname)
            Given a POST request is made to "/homograph/names/" with
                  """
                  {
                      "firstName": "david",
                      "lastSurname": "peterson"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/names/{id}"
                  }
                  """

      # POST request to create a student
            Given a POST request is made to "/homograph/students/" with
                  """
                  {
                      "schoolYearTypeReference": {
                          "schoolYear": "2025"
                      },
                      "studentNameReference": {
                          "firstName": "david",
                          "lastSurname": "peterson"
                      },
                      "address": {
                          "city": "Austin"
                      }
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/students/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolYearTypeReference": {
                          "schoolYear": "2025"
                      },
                      "studentNameReference": {
                          "firstName": "david",
                          "lastSurname": "peterson"
                      },
                      "address": {
                          "city": "Austin"
                      }
                  }
                  """

      # PUT request to update the student
             When a PUT request is made to "/homograph/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolYearTypeReference": {
                          "schoolYear": "2025"
                      },
                      "studentNameReference": {
                          "firstName": "david",
                          "lastSurname": "peterson"
                      },
                      "address": {
                          "city": "Dallas"
                      }
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolYearTypeReference": {
                          "schoolYear": "2025"
                      },
                      "studentNameReference": {
                          "firstName": "david",
                          "lastSurname": "peterson"
                      },
                      "address": {
                          "city": "Dallas"
                      }
                  }
                  """

      # DELETE request to delete the student
             When a DELETE request is made to "/homograph/students/{id}"
             Then it should respond with 204
      # Optionally, verify the resource is deleted
             When a GET request is made to "/homograph/students/{id}"
             Then it should respond with 404

        Scenario: 03 Ensure clients can create, update, and delete a student-school association in homograph extension
      # POST request to create a schoolYearType
            Given a POST request is made to "/homograph/schoolYearTypes/" with
                  """
                  {
                      "schoolYear": "2024",
                      "currentSchoolYear": true,
                      "schoolYearDescription": "2023-2024"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/schoolYearTypes/{id}"
                  }
                  """

      # POST request to create a name (firstName, lastSurname)
            Given a POST request is made to "/homograph/names/" with
                  """
                  {
                      "firstName": "geoff",
                      "lastSurname": "peterson"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/names/{id}"
                  }
                  """

      # POST request to create a student
            Given a POST request is made to "/homograph/students/" with
                  """
                  {
                      "schoolYearTypeReference": {
                          "schoolYear": "2024"
                      },
                      "studentNameReference": {
                          "firstName": "geoff",
                          "lastSurname": "peterson"
                      },
                      "address": {
                          "city": "Austin"
                      }
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/students/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolYearTypeReference": {
                          "schoolYear": "2024"
                      },
                      "studentNameReference": {
                          "firstName": "geoff",
                          "lastSurname": "peterson"
                      },
                      "address": {
                          "city": "Austin"
                      }
                  }
                  """

      # POST request to create a school (UT Austin)
            Given a POST request is made to "/homograph/schools/" with
                  """
                  {
                      "schoolName": "UT Austin"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/schools/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolName": "UT Austin"
                  }
                  """

      # POST request to create a school (UT Dallas)
            Given a POST request is made to "/homograph/schools/" with
                  """
                  {
                      "schoolName": "UT Dallas"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/schools/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolName": "UT Dallas"
                  }
                  """

      # POST request to create a student-school association (UT Austin)
            Given a POST request is made to "/homograph/studentSchoolAssociations/" with
                  """
                  {
                      "schoolReference": {
                          "schoolName": "UT Austin"
                      },
                      "studentReference": {
                          "studentFirstName": "geoff",
                          "studentLastSurname": "peterson"
                      }
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/homograph/studentSchoolAssociations/{id}"
                  }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolName": "UT Austin"
                      },
                      "studentReference": {
                          "studentFirstName": "geoff",
                          "studentLastSurname": "peterson"
                      }
                  }
                  """

      # PUT request to update the student-school association (change to UT Dallas)
             When a PUT request is made to "/homograph/studentSchoolAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolName": "UT Dallas"
                      },
                      "studentReference": {
                          "studentFirstName": "geoff",
                          "studentLastSurname": "peterson"
                      }
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolName": "UT Dallas"
                      },
                      "studentReference": {
                          "studentFirstName": "geoff",
                          "studentLastSurname": "peterson"
                      }
                  }
                  """

      # DELETE request to delete the student-school association
             When a DELETE request is made to "/homograph/studentSchoolAssociations/{id}"
             Then it should respond with 204

      # Optionally, verify the resource is deleted
             When a GET request is made to "/homograph/studentSchoolAssociations/{id}"
             Then it should respond with 404
