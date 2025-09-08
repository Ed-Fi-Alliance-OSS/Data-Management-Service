Feature: Kafka Messaging
    This feature demonstrates a simple Kafka testing approach. *Note* running these tests locally require a specific hosts entry: 127.0.0.1 dms-kafka1

        @api @kafka
        Scenario: 01 Test Kafka connectivity
            Given Kafka should be reachable
             Then Kafka consumer should be able to connect to topic "edfi.dms.document"

        @api @kafka
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
              And a Kafka message received on topic "edfi.dms.document" should contain in the edfidoc field
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "KAFKA_TEST_001",
                    "birthDate": "2009-01-01",
                    "firstName": "KafkaTest",
                    "lastSurname": "Student"
                  }
                  """
                  