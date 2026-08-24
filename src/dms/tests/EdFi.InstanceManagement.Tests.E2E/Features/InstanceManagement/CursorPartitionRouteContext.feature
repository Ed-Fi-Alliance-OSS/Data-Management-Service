# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @CursorPartitionRouteContext @instance-management-ci-shard-1
Feature: Routed cursor paging and partitions stay inside their tenant and route context
    Cursor walks and partition walks composed through the same tenant segment and route qualifiers the
    rest of the data plane uses. Within Tenant_255901, routes 255901/2024 and 255901/2025 map to
    separate databases; route 255902/2024 belongs to Tenant_255902. Each is seeded with one distinctly
    named descriptor, and each walk must return only its own.

    This is isolation coverage rather than another sizing test: the route-context deployment ships the
    ordinary maximum page size and the default partition count, so one partition per context is the
    expected shape. The multi-partition proof belongs to the dedicated sizing lane.

    absenceEventCategoryDescriptors is the seeded collection because the assertion is an exact union
    over everything the walk returns, and no other scenario in this suite writes to it. Scenario
    cleanup removes tenants, data stores, and applications, not data-plane documents, so a collection
    another scenario also seeds would carry that scenario's descriptors into this walk.

        Background:
            Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "absenceEventCategoryDescriptors" with body:
                  """
                  {
                      "codeValue": "RouteWalk-255901-2024",
                      "shortDescription": "Route walk 255901 2024",
                      "description": "Route walk 255901 2024",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor"
                  }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2025" resource "absenceEventCategoryDescriptors" with body:
                  """
                  {
                      "codeValue": "RouteWalk-255901-2025",
                      "shortDescription": "Route walk 255901 2025",
                      "description": "Route walk 255901 2025",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor"
                  }
                  """
             Then it should respond with success
            Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
             When a POST request is made to tenant "Tenant_255902" instance "255902/2024" resource "absenceEventCategoryDescriptors" with body:
                  """
                  {
                      "codeValue": "RouteWalk-255902-2024",
                      "shortDescription": "Route walk 255902 2024",
                      "description": "Route walk 255902 2024",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor"
                  }
                  """
             Then it should respond with success

        Scenario: Cursor walks and partition walks return only their own route context's descriptor
            Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
              # First route context of the first tenant.
             When a routed cursor walk is made for instance "255901/2024" resource "absenceEventCategoryDescriptors"
             Then the routed walk returned exactly the code value "RouteWalk-255901-2024"
             When the routed partitions are walked for instance "255901/2024" resource "absenceEventCategoryDescriptors"
             Then the routed walk returned exactly the code value "RouteWalk-255901-2024"
              # Second route context of the same tenant: a different database behind the same credentials,
              # so a qualifier dropped anywhere in the walk would surface the other year's descriptor.
             When a routed cursor walk is made for instance "255901/2025" resource "absenceEventCategoryDescriptors"
             Then the routed walk returned exactly the code value "RouteWalk-255901-2025"
             When the routed partitions are walked for instance "255901/2025" resource "absenceEventCategoryDescriptors"
             Then the routed walk returned exactly the code value "RouteWalk-255901-2025"
              # A second tenant, with its own credentials.
            Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
             When a routed cursor walk is made for instance "255902/2024" resource "absenceEventCategoryDescriptors"
             Then the routed walk returned exactly the code value "RouteWalk-255902-2024"
             When the routed partitions are walked for instance "255902/2024" resource "absenceEventCategoryDescriptors"
             Then the routed walk returned exactly the code value "RouteWalk-255902-2024"
