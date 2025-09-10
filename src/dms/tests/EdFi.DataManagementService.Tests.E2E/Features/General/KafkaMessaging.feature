Feature: Kafka Messaging
    This feature demonstrates a simple Kafka testing approach. *Note* running these tests locally require a specific hosts entry: 127.0.0.1 dms-kafka1

        @kafka
        Scenario: 01 Test Kafka connectivity
            Given Kafka should be reachable
             Then Kafka consumer should be able to connect to topic "edfi.dms.document"

        @kafka
        Scenario: 02 Creating a student should generate Kafka message
            Given I start collecting Kafka messages
              And the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "KAFKA_TEST_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaTest",
                    "lastSurname": "Student"
                  }
                  """
             Then it should respond with 201
              And a Kafka message should have the deleted flag "false" and EdFiDoc
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "KAFKA_TEST_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaTest",
                    "lastSurname": "Student"
                  }
                  """

        @kafka
        Scenario: 03 Updating a student should generate Kafka message
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "KAFKA_TEST_UPDATE_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaUpdate",
                    "lastSurname": "Student"
                  }
                  """
             Then it should respond with 201
            Given I start collecting Kafka messages
             When a PUT request is made to "ed-fi/students/{id}" with
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "KAFKA_TEST_UPDATE_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaUpdated",
                    "lastSurname": "UpdatedStudent"
                  }
                  """
             Then it should respond with 204
              And a Kafka message should have the deleted flag "false" and EdFiDoc
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "KAFKA_TEST_UPDATE_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaUpdated",
                    "lastSurname": "UpdatedStudent"
                  }
                  """

        @kafka
        Scenario: 04 Deleting a student should generate Kafka message
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "KAFKA_TEST_DELETE_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaDelete",
                    "lastSurname": "Student"
                  }
                  """
             Then it should respond with 201
            Given I start collecting Kafka messages
             When a DELETE request is made to "ed-fi/students/{id}"
             Then it should respond with 204
              And a Kafka message should have the deleted flag "true" and EdFiDoc
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "KAFKA_TEST_DELETE_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaDelete",
                    "lastSurname": "Student"
                  }
                  """
                  