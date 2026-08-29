# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @derivative-routing @instance-management-ci-shard-1
Feature: Snapshot and read-replica routing across the deployed stack
              Verify that a request to a data store carrying a read replica and a snapshot is served by
    the derivative the request selects, through real CMS derivative registration and the deployed DMS.
    The suite-owned fixture attaches both derivatives to one route, pointing them at two other fixture
    route databases. Those two routes are ordinary and writable, so each scenario seeds the replica and
    the snapshot through the production API and then names which physical database answered.

    Every assertion is on a value this feature wrote, never on a count, so unrelated data left in a
    shared route database by another feature cannot change the outcome.

        Background:
            Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
              And the authenticated tenant owns the derivative-routing route

        Scenario: 01 GET-many is served by the read replica, and Use-Snapshot selects the snapshot instead
             When a POST request is made to the "read replica" database resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "DerivRouting-GetMany-Replica",
                      "shortDescription": "Held only by the read replica",
                      "description": "Held only by the read replica",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
             When a POST request is made to the "snapshot" database resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "DerivRouting-GetMany-Snapshot",
                      "shortDescription": "Held only by the snapshot",
                      "description": "Held only by the snapshot",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
            # A replica-eligible read with no header is served by the configured read replica.
             When a GET request is made to the derivative route resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "DerivRouting-GetMany-Replica"
              And the response should not contain "DerivRouting-GetMany-Snapshot"
            # The same read asking for a snapshot is served by the snapshot, overriding the configured
            # read replica. This is the snapshot-precedence case.
             When a GET request is made to the derivative route resource "contentClassDescriptors" with Use-Snapshot "true"
             Then it should respond with 200
              And the response should contain "DerivRouting-GetMany-Snapshot"
              And the response should not contain "DerivRouting-GetMany-Replica"

        Scenario: 02 GET-by-id is served by the selected target
             When a POST request is made to the "read replica" database resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "DerivRouting-ById-Replica",
                      "shortDescription": "Held only by the read replica",
                      "description": "Held only by the read replica",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
             When the id from the response location is captured as "replicaOnlyId"
             When a POST request is made to the "snapshot" database resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "DerivRouting-ById-Snapshot",
                      "shortDescription": "Held only by the snapshot",
                      "description": "Held only by the snapshot",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
             When the id from the response location is captured as "snapshotOnlyId"
            # The replica holds this document, so the unheadered by-id read finds it.
             When a GET by id request is made to the derivative route resource "contentClassDescriptors" using captured "replicaOnlyId"
             Then it should respond with 200
              And the response should contain "DerivRouting-ById-Replica"
            # The snapshot holds the other one, so the same read finds it only with the header.
             When a GET by id request is made to the derivative route resource "contentClassDescriptors" using captured "snapshotOnlyId" with Use-Snapshot "true"
             Then it should respond with 200
              And the response should contain "DerivRouting-ById-Snapshot"
            # Proving the two reads reached different databases: each id is absent from the other target.
             When a GET by id request is made to the derivative route resource "contentClassDescriptors" using captured "snapshotOnlyId"
             Then it should respond with 404
             When a GET by id request is made to the derivative route resource "contentClassDescriptors" using captured "replicaOnlyId" with Use-Snapshot "true"
             Then it should respond with 404

        Scenario: 03 The deletes surface is served by the selected target
             When a POST request is made to the "read replica" database resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "DerivRouting-Deletes-Replica",
                      "shortDescription": "Deleted in the read replica",
                      "description": "Deleted in the read replica",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
              And the location should be stored as "replicaDeleteTarget"
             When a DELETE request is made for stored location "replicaDeleteTarget"
             Then it should respond with 204
             When a POST request is made to the "snapshot" database resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "DerivRouting-Deletes-Snapshot",
                      "shortDescription": "Deleted in the snapshot",
                      "description": "Deleted in the snapshot",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
              And the location should be stored as "snapshotDeleteTarget"
             When a DELETE request is made for stored location "snapshotDeleteTarget"
             Then it should respond with 204
             When a GET request is made to the derivative route "deletes" for resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "DerivRouting-Deletes-Replica"
              And the response should not contain "DerivRouting-Deletes-Snapshot"
             When a GET request is made to the derivative route "deletes" for resource "contentClassDescriptors" with Use-Snapshot "true"
             Then it should respond with 200
              And the response should contain "DerivRouting-Deletes-Snapshot"
              And the response should not contain "DerivRouting-Deletes-Replica"

        Scenario: 04 The keyChanges surface is served by the selected target
            # A key change needs a real identity update, so each database gets its own school and class
            # period. The school ids and period names are unique to this feature.
             When a POST request is made to the "read replica" database resource "gradeLevelDescriptors" with body:
                  """
                  { "codeValue": "Tenth Grade", "shortDescription": "Tenth Grade", "description": "Tenth Grade", "namespace": "uri://ed-fi.org/GradeLevelDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to the "read replica" database resource "educationOrganizationCategoryDescriptors" with body:
                  """
                  { "codeValue": "School", "shortDescription": "School", "description": "School", "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to the "read replica" database resource "schools" with body:
                  """
                  {
                    "schoolId": 2559010901,
                    "nameOfInstitution": "Derivative Routing Replica School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with success
             When a POST request is made to the "read replica" database resource "classPeriods" with body:
                  """
                  { "classPeriodName": "DerivRouting-KeyChanges-Replica-A", "schoolReference": { "schoolId": 2559010901 } }
                  """
             Then it should respond with success
              And the location should be stored as "replicaClassPeriod"
             When a PUT request is made for stored location "replicaClassPeriod" with body:
                  """
                  { "id": "{id}", "classPeriodName": "DerivRouting-KeyChanges-Replica-B", "schoolReference": { "schoolId": 2559010901 } }
                  """
             Then it should respond with 204
             When a POST request is made to the "snapshot" database resource "gradeLevelDescriptors" with body:
                  """
                  { "codeValue": "Tenth Grade", "shortDescription": "Tenth Grade", "description": "Tenth Grade", "namespace": "uri://ed-fi.org/GradeLevelDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to the "snapshot" database resource "educationOrganizationCategoryDescriptors" with body:
                  """
                  { "codeValue": "School", "shortDescription": "School", "description": "School", "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to the "snapshot" database resource "schools" with body:
                  """
                  {
                    "schoolId": 2559010902,
                    "nameOfInstitution": "Derivative Routing Snapshot School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with success
             When a POST request is made to the "snapshot" database resource "classPeriods" with body:
                  """
                  { "classPeriodName": "DerivRouting-KeyChanges-Snapshot-A", "schoolReference": { "schoolId": 2559010902 } }
                  """
             Then it should respond with success
              And the location should be stored as "snapshotClassPeriod"
             When a PUT request is made for stored location "snapshotClassPeriod" with body:
                  """
                  { "id": "{id}", "classPeriodName": "DerivRouting-KeyChanges-Snapshot-B", "schoolReference": { "schoolId": 2559010902 } }
                  """
             Then it should respond with 204
             When a GET request is made to the derivative route "keyChanges" for resource "classPeriods"
             Then it should respond with 200
              And the response should contain "DerivRouting-KeyChanges-Replica-B"
              And the response should not contain "DerivRouting-KeyChanges-Snapshot"
             When a GET request is made to the derivative route "keyChanges" for resource "classPeriods" with Use-Snapshot "true"
             Then it should respond with 200
              And the response should contain "DerivRouting-KeyChanges-Snapshot-B"
              And the response should not contain "DerivRouting-KeyChanges-Replica"

        Scenario: 05 availableChangeVersions reports the selected target, and the snapshot overrides the replica
            # This scenario compares each routed read against itself before and after a write to one
            # database only. That is what makes it decisive: the two databases' change-version counters
            # are independent, so their absolute values may coincide, but only the target that received
            # the write can report a higher value afterwards.
             When I capture the newest change version for the derivative route as "routedBefore"
              And I capture the newest change version for the derivative route with Use-Snapshot "true" as "routedSnapshotBefore"
             When a POST request is made to the "snapshot" database resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "DerivRouting-ChangeVersion-Snapshot",
                      "shortDescription": "Advances only the snapshot",
                      "description": "Advances only the snapshot",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
            # The snapshot-requesting read is answered by the snapshot, so it sees the write.
             When I capture the newest change version for the derivative route with Use-Snapshot "true" as "routedSnapshotAfter"
             Then the captured change version "routedSnapshotAfter" is greater than the captured change version "routedSnapshotBefore"
            # The unheadered read is answered by the read replica, which received nothing, so it is
            # unchanged. Snapshot selection therefore overrode the configured read replica.
             When I capture the newest change version for the derivative route as "routedAfter"
             Then the captured change version "routedAfter" equals the captured change version "routedBefore"
