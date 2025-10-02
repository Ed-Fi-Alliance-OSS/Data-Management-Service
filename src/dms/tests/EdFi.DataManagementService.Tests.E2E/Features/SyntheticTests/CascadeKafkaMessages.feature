Feature: Synthetic Test - Cascade Kafka Messages

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And I start collecting Kafka messages
              And Kafka should be reachable

        @kafka
        Scenario: 01 Single referencing document cascade update
            Given a synthetic core schema with project "Ed-Fi"
              And the schema has resource "School" with identity "schoolId"
              And the schema has resource "Student" with identity "studentUniqueId"
              And the "Student" resource has reference "schoolReference" to "School"
             When the schema is deployed to the DMS
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "123",
                    "nameOfInstitution": "Test School"
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "STUDENT001",
                    "firstName": "John",
                    "lastSurname": "Doe",
                    "schoolReference": {
                      "schoolId": "123"
                    }
                  }
                  """
             Then it should respond with 200 or 201
             When a PUT request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                    "id":"{id}",
                    "schoolId": "456",
                    "nameOfInstitution": "Updated Test School"
                  }
                  """
             Then it should respond with 204
              And multiple Kafka messages should be received for cascade update

        Scenario: 02 Multiple referencing documents cascade update
            Given a synthetic core schema with project "Ed-Fi"
              And the schema has resource "School" with identity "schoolId"
              And the schema has resource "Student" with identity "studentUniqueId"
              And the schema has resource "Staff" with identity "staffUniqueId"
              And the "Student" resource has reference "schoolReference" to "School"
              And the "Staff" resource has reference "schoolReference" to "School"
             When the schema is deployed to the DMS
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "100",
                    "nameOfInstitution": "Multi Reference School"
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "STUDENT100",
                    "firstName": "Alice",
                    "lastSurname": "Smith",
                    "schoolReference": {
                      "schoolId": "100"
                    }
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "STUDENT101",
                    "firstName": "Bob",
                    "lastSurname": "Johnson",
                    "schoolReference": {
                      "schoolId": "100"
                    }
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/staff" with
                  """
                  {
                    "staffUniqueId": "STAFF100",
                    "firstName": "Carol",
                    "lastSurname": "Wilson",
                    "schoolReference": {
                      "schoolId": "100"
                    }
                  }
                  """
             Then it should respond with 200 or 201
             When a PUT request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                    "id": "{id}",
                    "schoolId": "200",
                    "nameOfInstitution": "Updated Multi Reference School"
                  }
                  """
             Then it should respond with 204
              And multiple Kafka messages should be received for cascade update with 4 total messages

        Scenario: 03 Multi-level cascade update
            Given a synthetic core schema with project "Ed-Fi"
              And the schema has resource "District" with identity "districtId"
              And the schema has resource "School" with identity "schoolId"
              And the schema has resource "Student" with identity "studentUniqueId"
              And the "School" resource has reference "districtReference" to "District"
              And the "Student" resource has reference "schoolReference" to "School"
             When the schema is deployed to the DMS
              And a POST request is made to "/ed-fi/districts" with
                  """
                  {
                    "districtId": "DIST001",
                    "nameOfInstitution": "Test District"
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "SCH001",
                    "nameOfInstitution": "District School",
                    "districtReference": {
                      "districtId": "DIST001"
                    }
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "STUDENT200",
                    "firstName": "David",
                    "lastSurname": "Brown",
                    "schoolReference": {
                      "schoolId": "SCH001"
                    }
                  }
                  """
             Then it should respond with 200 or 201
             When a PUT request is made to "/ed-fi/districts/{id}" with
                  """
                  {
                    "id": "{id}",
                    "districtId": "DIST002",
                    "nameOfInstitution": "Updated Test District"
                  }
                  """
             Then it should respond with 204
              And multiple Kafka messages should be received for multi-level cascade update

        Scenario: 04 Complex cascade with associations
            Given a synthetic core schema with project "Ed-Fi"
              And the schema has resource "School" with identity "schoolId"
              And the schema has resource "Student" with identity "studentUniqueId"
              And the schema has resource "Staff" with identity "staffUniqueId"
              And the schema has resource "StudentSchoolAssociation" with identities
                  | identity        |
                  | studentUniqueId |
                  | schoolId        |
              And the schema has resource "StaffSchoolAssociation" with identities
                  | identity      |
                  | staffUniqueId |
                  | schoolId      |
             When the schema is deployed to the DMS
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "500",
                    "nameOfInstitution": "Association School"
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "STU500",
                    "firstName": "Emma",
                    "lastSurname": "Davis"
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/staff" with
                  """
                  {
                    "staffUniqueId": "STAFF500",
                    "firstName": "Frank",
                    "lastSurname": "Miller"
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "studentUniqueId": "STU500",
                    "schoolId": "500",
                    "entryDate": "2024-08-15"
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                  {
                    "staffUniqueId": "STAFF500",
                    "schoolId": "500"
                  }
                  """
             Then it should respond with 200 or 201
             When a PUT request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                    "id": "{id}",
                    "schoolId": "600",
                    "nameOfInstitution": "Updated Association School"
                  }
                  """
             Then it should respond with 204
              And multiple Kafka messages should be received for association cascade update
