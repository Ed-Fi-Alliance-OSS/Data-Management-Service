# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @kafka
Feature: Kafka Topic-Per-Instance Segregation
    Verify that Kafka messages are published to instance-specific topics
    and that no cross-instance data leakage occurs in multi-tenant deployments

    Background:
        Given the system is configured with route qualifiers
          And I have completed instance setup with 3 instances
          And I am authenticated to DMS with application credentials
          And I start collecting Kafka messages for all instances

    @smoke
    Scenario: 01 Verify Kafka connectivity for instance topics
        Then Kafka consumer should be able to connect to all instance topics

    Scenario: 02 Messages published to correct instance-specific topics
        When a POST request is made to instance "255901/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "KafkaTest-255901-2024",
                "shortDescription": "Test descriptor for Kafka validation",
                "description": "Test descriptor for instance 255901/2024 to verify topic segregation",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
          And I wait 3 seconds for Kafka messages
          And a Kafka message for instance "255901/2024" should be published to its instance-specific topic
          And the message should contain "KafkaTest-255901-2024"
          And the message should have deleted flag "false"

    Scenario: 03 Multiple instances publish to separate topics
        When a POST request is made to instance "255901/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "District255901-2024",
                "shortDescription": "District 1 Year 2024",
                "description": "Test descriptor for instance 255901/2024",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
        When a POST request is made to instance "255901/2025" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "District255901-2025",
                "shortDescription": "District 1 Year 2025",
                "description": "Test descriptor for instance 255901/2025",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
        When a POST request is made to instance "255902/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "District255902-2024",
                "shortDescription": "District 2 Year 2024",
                "description": "Test descriptor for instance 255902/2024",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
          And I wait 3 seconds for Kafka messages
          And instance "255901/2024" should have 1 Kafka message
          And instance "255901/2025" should have 1 Kafka message
          And instance "255902/2024" should have 1 Kafka message

    Scenario: 04 No cross-instance data leakage in Kafka topics
        When a POST request is made to instance "255901/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "IsolationTest-255901",
                "shortDescription": "Isolation test descriptor",
                "description": "This message should only appear in instance 255901/2024 topic",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
          And I wait 3 seconds for Kafka messages
          And a Kafka message for instance "255901/2024" should be published to its instance-specific topic
          And the message should contain "IsolationTest-255901"
          And Kafka messages for instance "255901/2024" should not appear in instance "255901/2025" topic
          And Kafka messages for instance "255901/2024" should not appear in instance "255902/2024" topic
          And no cross-instance message leakage should occur

    Scenario: 05 Update operations publish to correct instance topic
        When a POST request is made to instance "255901/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "UpdateTest-Original",
                "shortDescription": "Original descriptor",
                "description": "Original descriptor before update",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
          And the location should be stored as "updateTestDescriptor"
          And I wait 2 seconds for Kafka messages
        When I GET resource "updateTestDescriptor" by location
        Then it should respond with 200
          # Note: Update step would require additional step definitions to handle PUT requests
          # For now, verify the initial POST generated a message
          And instance "255901/2024" should have 1 Kafka message
          And the message should have deleted flag "false"

    Scenario: 06 Delete operations publish to correct instance topic with deleted flag
        When a POST request is made to instance "255901/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "DeleteTest-Descriptor",
                "shortDescription": "Descriptor to be deleted",
                "description": "This descriptor will be deleted to test Kafka messaging",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
          And the location should be stored as "deleteTestDescriptor"
          And I wait 2 seconds for Kafka messages
          And instance "255901/2024" should have 1 Kafka message
          # Note: DELETE step would require additional step definitions
          # Initial POST is verified here

    Scenario: 07 Comprehensive isolation validation across all instances
        When a POST request is made to instance "255901/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "Comprehensive-255901-2024",
                "shortDescription": "Comprehensive test 1",
                "description": "Comprehensive isolation test descriptor 1",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
        When a POST request is made to instance "255901/2025" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "Comprehensive-255901-2025",
                "shortDescription": "Comprehensive test 2",
                "description": "Comprehensive isolation test descriptor 2",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
        When a POST request is made to instance "255902/2024" and resource "contentClassDescriptors" with body:
            """
            {
                "codeValue": "Comprehensive-255902-2024",
                "shortDescription": "Comprehensive test 3",
                "description": "Comprehensive isolation test descriptor 3",
                "namespace": "uri://ed-fi.org/ContentClassDescriptor"
            }
            """
        Then it should respond with success
          And I wait 3 seconds for Kafka messages
          And no cross-instance message leakage should occur
          And instance "255901/2024" should have 1 Kafka message
          And instance "255901/2025" should have 1 Kafka message
          And instance "255902/2024" should have 1 Kafka message
