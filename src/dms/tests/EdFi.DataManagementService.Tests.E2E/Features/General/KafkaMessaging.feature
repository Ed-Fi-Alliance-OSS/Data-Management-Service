Feature: Kafka Messaging
    This feature demonstrates a simple Kafka testing approach:

        @api @kafka
        Scenario: Test Kafka connectivity
            Given Kafka should be reachable on localhost:9092

        @api @kafka
        Scenario: Verify Kafka consumer can read messages from edfi.dms.document topic
             Then Kafka consumer should be able to connect to topic "edfi.dms.document"

        @api @kafka
        Scenario: Creating a student should generate Kafka message
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
                  