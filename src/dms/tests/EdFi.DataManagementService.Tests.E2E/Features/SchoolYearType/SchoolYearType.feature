Feature: SchoolYearType resource

  Background:
    Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"

  Rule: SchoolYearType resource
  @ignore @DMS-651
  Scenario: 01 Ensure clients can get a SchoolYearType
        When a GET request is made to "/ed-fi/schoolYearTypes?limit=47"
        Then it should respond with 200
        And the response should contain at most 47 SchoolYearTypes
 @ignore @DMS-651
  Scenario: 02 Filter SchoolYearTypes by schoolYear
      When a GET request is made to "/ed-fi/schoolYearTypes?schoolYear=2025"
      Then it should respond with 200
 Scenario: 03 Ensure clients can create/update/get/delete a schoolYearTypes resource
      # POST request to create a school
      When a POST request is made to "/ed-fi/schoolYearTypes/" with
        """
        {
            "schoolYear": 2199,
            "currentSchoolYear": false,
            "schoolYearDescription": "2198-2199"
        }
        """
      Then it should respond with 201 or 200
      And the response headers include
        """
        {
            "location": "/ed-fi/schoolYearTypes/{id}"
        }
        """
      And the record can be retrieved with a GET request
        """
        {
            "id": "{id}",
            "schoolYear": 2199,
            "currentSchoolYear": false,
            "schoolYearDescription": "2198-2199"
        }
        """

      # PUT request to update the school
      When a PUT request is made to "/ed-fi/schoolYearTypes/{id}" with
        """
        {
            "id": "{id}",
            "schoolYear": 2199,
            "currentSchoolYear": false,
            "schoolYearDescription": "2198-2199"
        }
        """
      Then it should respond with 204
      And the record can be retrieved with a GET request
        """
        {
            "id": "{id}",
            "schoolYear": 2199,
            "currentSchoolYear": false,
            "schoolYearDescription": "2198-2199"
        }
        """

      # DELETE request to delete the school
      When a DELETE request is made to "/ed-fi/schoolYearTypes/{id}"
      Then it should respond with 204
      # Optionally, verify the resource is deleted
      When a GET request is made to "/ed-fi/schoolYearTypes/{id}"
      Then it should respond with 404


