# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Route Qualifier Data Segregation
              Verify that data is properly segregated between instances using route qualifiers

        Background:
            Given the system is configured with route qualifiers
              And I have completed instance setup with 3 instances
              And I am authenticated to DMS with application credentials
             When a POST request is made to instance "255901/2024" and resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "District255901-2024",
                      "shortDescription": "Test descriptor for District 255901 Year 2024",
                      "description": "Test descriptor for District 255901 Year 2024",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
              And the location should be stored as "descriptor1"
             When a POST request is made to instance "255901/2025" and resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "District255901-2025",
                      "shortDescription": "Test descriptor for District 255901 Year 2025",
                      "description": "Test descriptor for District 255901 Year 2025",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
              And the location should be stored as "descriptor2"
             When a POST request is made to instance "255902/2024" and resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "District255902-2024",
                      "shortDescription": "Test descriptor for District 255902 Year 2024",
                      "description": "Test descriptor for District 255902 Year 2024",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
              And the location should be stored as "descriptor3"

        Scenario: Retrieve descriptors are isolated by route qualifiers
             When a GET request is made to instance "255901/2024" and resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "District255901-2024"
              And the response should not contain "District255901-2025"
              And the response should not contain "District255902-2024"
             When a GET request is made to instance "255901/2025" and resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "District255901-2025"
              And the response should not contain "District255901-2024"
              And the response should not contain "District255902-2024"
             When a GET request is made to instance "255902/2024" and resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "District255902-2024"
              And the response should not contain "District255901-2024"
              And the response should not contain "District255901-2025"

        Scenario: Retrieve specific resource by location maintains context
             When I GET resource "descriptor1" by location
             Then it should respond with 200
              And the response should contain "District255901-2024"
             When I GET resource "descriptor2" by location
             Then it should respond with 200
              And the response should contain "District255901-2025"
             When I GET resource "descriptor3" by location
             Then it should respond with 200
              And the response should contain "District255902-2024"
