# DMS-1025 Descriptor Runtime Integration Coverage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add focused PostgreSQL and SQL Server API integration coverage for descriptor runtime create, update, query, paging, and descriptor-reference behavior.

**Architecture:** Add an integration-owned `DescriptorRuntime` fixture derived from the existing profile-root-only-merge shape, but with a full shared descriptor query contract. Add one scenario class with all HTTP and database assertions, plus thin per-dialect wrappers that bind the scenario to the existing API integration harness.

**Tech Stack:** .NET 10, C#, NUnit, FluentAssertions, `WebApplicationFactory`, real PostgreSQL and SQL Server integration databases through `EdFi.DataManagementService.Tests.Integration`.

---

## File Structure

- Create `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/fixture.json`
  - Declares the new fixture input and supported dialects.
- Create `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/inputs/ApiSchema.json`
  - Holds the descriptor runtime ApiSchema fixture.
  - Uses `Student`, `SchoolTypeDescriptor`, and `ProfileRootOnlyMergeItem`.
  - Adds the exact seven-field descriptor query contract required by `DescriptorQueryCapabilityCompiler`.
- Modify `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs`
  - Adds `FixtureKey.DescriptorRuntime`.
- Modify `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs`
  - Maps `FixtureKey.DescriptorRuntime` to the new integration-owned fixture path.
- Create `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs`
  - Owns all descriptor API requests, response assertions, and metadata database assertions.
- Create `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_DescriptorRuntime.cs`
  - Thin PostgreSQL wrapper, one `[Test]` per scenario method.
- Create `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_DescriptorRuntime.cs`
  - Thin SQL Server wrapper, one `[Test]` per scenario method.
- Modify `src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md`
  - Adds the fixture to the fixture map.

Do not add `--seed-descriptors` implementation or tests in this plan. That CLI capability belongs to `DMS-955` and is not present on this branch.

---

### Task 1: Add DescriptorRuntime Fixture

**Files:**
- Create: `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/fixture.json`
- Create: `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/inputs/ApiSchema.json`
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs`
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs`

- [ ] **Step 1: Create the fixture directory**

Run:

```powershell
New-Item -ItemType Directory -Force src\dms\backend\EdFi.DataManagementService.Backend.IntegrationFixtures\descriptor-runtime\inputs
```

Expected: the command succeeds and creates the `descriptor-runtime\inputs` directory.

- [ ] **Step 2: Create the fixture manifest**

Create `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/fixture.json` with this exact content:

```json
{
  "apiSchemaFiles": ["ApiSchema.json"],
  "dialects": ["pgsql", "mssql"]
}
```

- [ ] **Step 3: Create the fixture ApiSchema**

Create `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/inputs/ApiSchema.json` with this exact content:

```json
{
  "apiSchemaVersion": "1.0.0",
  "projectSchema": {
    "projectName": "Ed-Fi",
    "projectEndpointName": "ed-fi",
    "projectVersion": "5.0.0",
    "isExtensionProject": false,
    "description": "Descriptor runtime fixture: exercises descriptor writes, shared descriptor table queries, metadata preservation, and descriptor reference resolution.",
    "abstractResources": {},
    "caseInsensitiveEndpointNameMapping": {
      "students": "students",
      "schooltypedescriptors": "schoolTypeDescriptors",
      "profilerootonlymergeitems": "profileRootOnlyMergeItems"
    },
    "educationOrganizationHierarchy": {},
    "educationOrganizationTypes": [],
    "resourceNameMapping": {
      "Student": "students",
      "SchoolTypeDescriptor": "schoolTypeDescriptors",
      "ProfileRootOnlyMergeItem": "profileRootOnlyMergeItems"
    },
    "resourceSchemas": {
      "students": {
        "resourceName": "Student",
        "isDescriptor": false,
        "isResourceExtension": false,
        "isSubclass": false,
        "isSchoolYearEnumeration": false,
        "allowIdentityUpdates": false,
        "arrayUniquenessConstraints": [],
        "equalityConstraints": [],
        "identityJsonPaths": [
          "$.studentUniqueId"
        ],
        "documentPathsMapping": {
          "StudentUniqueId": {
            "isReference": false,
            "isPartOfIdentity": true,
            "isRequired": true,
            "path": "$.studentUniqueId"
          },
          "FirstName": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": true,
            "path": "$.firstName"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "studentUniqueId": {
              "type": "string",
              "maxLength": 32
            },
            "firstName": {
              "type": "string",
              "maxLength": 75
            }
          },
          "required": [
            "studentUniqueId",
            "firstName"
          ]
        }
      },
      "schoolTypeDescriptors": {
        "resourceName": "SchoolTypeDescriptor",
        "isDescriptor": true,
        "isResourceExtension": false,
        "isSubclass": false,
        "isSchoolYearEnumeration": false,
        "allowIdentityUpdates": false,
        "arrayUniquenessConstraints": [],
        "equalityConstraints": [],
        "identityJsonPaths": [
          "$.namespace",
          "$.codeValue"
        ],
        "documentPathsMapping": {
          "Namespace": {
            "isReference": false,
            "isPartOfIdentity": true,
            "isRequired": true,
            "path": "$.namespace"
          },
          "CodeValue": {
            "isReference": false,
            "isPartOfIdentity": true,
            "isRequired": true,
            "path": "$.codeValue"
          },
          "ShortDescription": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": true,
            "path": "$.shortDescription"
          },
          "Description": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": false,
            "path": "$.description"
          },
          "EffectiveBeginDate": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": false,
            "path": "$.effectiveBeginDate"
          },
          "EffectiveEndDate": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": false,
            "path": "$.effectiveEndDate"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "namespace": {
              "type": "string",
              "maxLength": 255,
              "minLength": 1
            },
            "codeValue": {
              "type": "string",
              "maxLength": 50,
              "minLength": 1
            },
            "shortDescription": {
              "type": "string",
              "maxLength": 75,
              "minLength": 1
            },
            "description": {
              "type": "string",
              "maxLength": 1024
            },
            "effectiveBeginDate": {
              "type": "string",
              "format": "date"
            },
            "effectiveEndDate": {
              "type": "string",
              "format": "date"
            }
          },
          "required": [
            "namespace",
            "codeValue",
            "shortDescription"
          ]
        },
        "queryFieldMapping": {
          "id": [
            {
              "path": "$.id",
              "type": "string"
            }
          ],
          "namespace": [
            {
              "path": "$.namespace",
              "type": "string"
            }
          ],
          "codeValue": [
            {
              "path": "$.codeValue",
              "type": "string"
            }
          ],
          "shortDescription": [
            {
              "path": "$.shortDescription",
              "type": "string"
            }
          ],
          "description": [
            {
              "path": "$.description",
              "type": "string"
            }
          ],
          "effectiveBeginDate": [
            {
              "path": "$.effectiveBeginDate",
              "type": "date"
            }
          ],
          "effectiveEndDate": [
            {
              "path": "$.effectiveEndDate",
              "type": "date"
            }
          ]
        }
      },
      "profileRootOnlyMergeItems": {
        "resourceName": "ProfileRootOnlyMergeItem",
        "isDescriptor": false,
        "isResourceExtension": false,
        "isSubclass": false,
        "isSchoolYearEnumeration": false,
        "allowIdentityUpdates": false,
        "arrayUniquenessConstraints": [],
        "equalityConstraints": [
          {
            "sourceJsonPath": "$.primarySchoolTypeDescriptor",
            "targetJsonPath": "$.secondarySchoolTypeDescriptor"
          }
        ],
        "identityJsonPaths": [
          "$.profileRootOnlyMergeItemId"
        ],
        "documentPathsMapping": {
          "ProfileRootOnlyMergeItemId": {
            "isReference": false,
            "isPartOfIdentity": true,
            "isRequired": true,
            "path": "$.profileRootOnlyMergeItemId"
          },
          "DisplayName": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": false,
            "path": "$.displayName"
          },
          "ProfileScope.ClearableText": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": false,
            "path": "$.profileScope.clearableText"
          },
          "ProfileScope.PreservedText": {
            "isReference": false,
            "isPartOfIdentity": false,
            "isRequired": false,
            "path": "$.profileScope.preservedText"
          },
          "StudentReference": {
            "isReference": true,
            "isDescriptor": false,
            "isPartOfIdentity": false,
            "isRequired": false,
            "projectName": "Ed-Fi",
            "resourceName": "Student",
            "referenceJsonPaths": [
              {
                "identityJsonPath": "$.studentUniqueId",
                "referenceJsonPath": "$.studentReference.studentUniqueId"
              }
            ]
          },
          "PrimarySchoolTypeDescriptor": {
            "isReference": true,
            "isDescriptor": true,
            "isPartOfIdentity": false,
            "isRequired": false,
            "projectName": "Ed-Fi",
            "resourceName": "SchoolTypeDescriptor",
            "path": "$.primarySchoolTypeDescriptor"
          },
          "SecondarySchoolTypeDescriptor": {
            "isReference": true,
            "isDescriptor": true,
            "isPartOfIdentity": false,
            "isRequired": false,
            "projectName": "Ed-Fi",
            "resourceName": "SchoolTypeDescriptor",
            "path": "$.secondarySchoolTypeDescriptor"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "profileRootOnlyMergeItemId": {
              "type": "integer"
            },
            "displayName": {
              "type": "string",
              "maxLength": 100
            },
            "profileScope": {
              "type": "object",
              "properties": {
                "clearableText": {
                  "type": "string",
                  "maxLength": 100
                },
                "preservedText": {
                  "type": "string",
                  "maxLength": 100
                }
              }
            },
            "studentReference": {
              "type": "object",
              "properties": {
                "studentUniqueId": {
                  "type": "string",
                  "maxLength": 32
                }
              },
              "required": ["studentUniqueId"]
            },
            "primarySchoolTypeDescriptor": {
              "type": "string",
              "maxLength": 306
            },
            "secondarySchoolTypeDescriptor": {
              "type": "string",
              "maxLength": 306
            }
          },
          "required": [
            "profileRootOnlyMergeItemId"
          ]
        }
      }
    }
  }
}
```

- [ ] **Step 4: Add the fixture enum value**

Modify `FixtureKey` in `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs` so it reads:

```csharp
public enum FixtureKey
{
    FocusedStableKeyUpdateSemantics,
    ProfileRootOnlyMerge,
    DescriptorRuntime,
    ProfileSeparateTableMerge,
    ProfileNestedAndRootExtensionChildren,
    ProfileCollectionAlignedExtension,
    ProfileCollectionAlignedExtensionHiddenDescendant,
}
```

- [ ] **Step 5: Map the fixture path**

Modify `_repoRelativePaths` in `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs` by adding this entry immediately after `ProfileRootOnlyMerge`:

```csharp
[FixtureKey.DescriptorRuntime] =
    "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime",
```

The surrounding dictionary should contain:

```csharp
[FixtureKey.ProfileRootOnlyMerge] =
    "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-root-only-merge",
[FixtureKey.DescriptorRuntime] =
    "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime",
[FixtureKey.ProfileSeparateTableMerge] =
    "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-separate-table-merge",
```

- [ ] **Step 6: Format touched C#**

Run:

```powershell
dotnet csharpier format src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs
```

Expected: command exits `0`.

- [ ] **Step 7: Run a compile check**

Run:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --no-restore --filter "FullyQualifiedName~NoMatchingDescriptorRuntimeTestYet"
```

Expected: command exits `0`. It may report no tests matched. This verifies the new fixture enum/path edits compile before adding scenario tests.

- [ ] **Step 8: Commit fixture infrastructure**

```powershell
git add src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs
git commit -m "test: add descriptor runtime fixture"
```

---

### Task 2: Add Descriptor Runtime Scenarios And Wrappers

**Files:**
- Create: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs`
- Create: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_DescriptorRuntime.cs`
- Create: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_DescriptorRuntime.cs`

- [ ] **Step 1: Create the scenario class**

Create `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs` with this content:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class DescriptorRuntimeScenario
{
    private const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string ProfileRootOnlyMergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";

    public static async Task It_creates_and_reads_a_descriptor(ApiIntegrationHarness harness)
    {
        var payload = new DescriptorPayload(
            DescriptorNamespace,
            "DMS-1025 Create",
            "DMS-1025 Create Short",
            "DMS-1025 Create Description",
            "2026-05-15",
            "2026-12-31"
        );

        CreatedDescriptor created = await CreateDescriptorAsync(harness, payload);

        created.LocationPath.Should().StartWith($"{DescriptorEndpoint}/");
        created.Etag.Should().NotBeNullOrWhiteSpace();

        JsonObject returned = await GetDescriptorAsync(harness, created.LocationPath);
        AssertDescriptorFields(returned, payload, created.Id);
        GetRequiredString(returned, "_etag").Should().NotBeNullOrWhiteSpace();
        GetRequiredString(returned, "_lastModifiedDate").Should().NotBeNullOrWhiteSpace();
    }

    public static async Task It_updates_descriptor_non_identity_fields_and_advances_metadata(
        ApiIntegrationHarness harness
    )
    {
        var initialPayload = new DescriptorPayload(
            DescriptorNamespace,
            "DMS-1025 Update",
            "DMS-1025 Update Short",
            "DMS-1025 Update Description",
            "2026-05-15",
            "2026-12-31"
        );
        CreatedDescriptor created = await CreateDescriptorAsync(harness, initialPayload);
        JsonObject initialGet = await GetDescriptorAsync(harness, created.LocationPath);
        DocumentMetadata initialMetadata = await ReadDocumentMetadataAsync(harness, created.Id);
        string initialGetEtag = GetRequiredString(initialGet, "_etag");

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var updatedPayload = new DescriptorPayload(
            DescriptorNamespace,
            "DMS-1025 Update",
            "DMS-1025 Update Short Changed",
            "DMS-1025 Update Description Changed",
            "2026-06-01",
            "2027-01-31"
        );

        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            created.LocationPath,
            created.Id,
            updatedPayload,
            initialGetEtag
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);
        putResponse.TryReadRawEtag(out string putEtag).Should().BeTrue("descriptor PUT must emit an ETag");
        putEtag.Should().NotBe(initialGetEtag, "changed descriptor content must advance the ETag");

        JsonObject updatedGet = await GetDescriptorAsync(harness, created.LocationPath);
        AssertDescriptorFields(updatedGet, updatedPayload, created.Id);
        GetRequiredString(updatedGet, "_etag").Should().NotBe(initialGetEtag);
        GetRequiredString(updatedGet, "_lastModifiedDate")
            .Should()
            .NotBe(GetRequiredString(initialGet, "_lastModifiedDate"));

        DocumentMetadata updatedMetadata = await ReadDocumentMetadataAsync(harness, created.Id);
        updatedMetadata.ContentVersion.Should().BeGreaterThan(initialMetadata.ContentVersion);
        updatedMetadata.ContentLastModifiedAt.Should().BeAfter(initialMetadata.ContentLastModifiedAt);
    }

    public static async Task It_preserves_metadata_for_unchanged_descriptor_put(
        ApiIntegrationHarness harness
    )
    {
        var payload = new DescriptorPayload(
            DescriptorNamespace,
            "DMS-1025 No Op",
            "DMS-1025 No Op Short",
            "DMS-1025 No Op Description",
            "2026-05-15",
            "2026-12-31"
        );
        CreatedDescriptor created = await CreateDescriptorAsync(harness, payload);
        JsonObject beforeGet = await GetDescriptorAsync(harness, created.LocationPath);
        DocumentMetadata beforeMetadata = await ReadDocumentMetadataAsync(harness, created.Id);
        string beforeEtag = GetRequiredString(beforeGet, "_etag");
        string beforeLastModifiedDate = GetRequiredString(beforeGet, "_lastModifiedDate");

        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            created.LocationPath,
            created.Id,
            payload,
            beforeEtag
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);

        JsonObject afterGet = await GetDescriptorAsync(harness, created.LocationPath);
        AssertDescriptorFields(afterGet, payload, created.Id);
        GetRequiredString(afterGet, "_etag").Should().Be(beforeEtag);
        GetRequiredString(afterGet, "_lastModifiedDate").Should().Be(beforeLastModifiedDate);

        DocumentMetadata afterMetadata = await ReadDocumentMetadataAsync(harness, created.Id);
        afterMetadata.ContentVersion.Should().Be(beforeMetadata.ContentVersion);
        afterMetadata.ContentLastModifiedAt.Should().Be(beforeMetadata.ContentLastModifiedAt);
    }

    public static async Task It_rejects_descriptor_identity_changes(ApiIntegrationHarness harness)
    {
        var payload = new DescriptorPayload(
            DescriptorNamespace,
            "DMS-1025 Identity",
            "DMS-1025 Identity Short",
            "DMS-1025 Identity Description",
            "2026-05-15",
            "2026-12-31"
        );
        CreatedDescriptor created = await CreateDescriptorAsync(harness, payload);
        JsonObject beforeGet = await GetDescriptorAsync(harness, created.LocationPath);
        string beforeEtag = GetRequiredString(beforeGet, "_etag");

        var changedIdentityPayload = new DescriptorPayload(
            DescriptorNamespace,
            "DMS-1025 Identity Changed",
            "DMS-1025 Identity Short",
            "DMS-1025 Identity Description",
            "2026-05-15",
            "2026-12-31"
        );

        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            created.LocationPath,
            created.Id,
            changedIdentityPayload,
            beforeEtag
        );
        string body = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonObject problem = ParseObject(body);
        GetRequiredString(problem, "type")
            .Should()
            .Be("urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported");
        GetRequiredString(problem, "title").Should().Be("Key Change Not Supported");
    }

    public static async Task It_filters_and_pages_descriptor_queries(ApiIntegrationHarness harness)
    {
        const string queryNamespace = "uri://ed-fi.org/SchoolTypeDescriptor/DMS-1025-Query";
        DescriptorPayload[] payloads =
        [
            new(
                queryNamespace,
                "Alpha",
                "DMS-1025 Query Alpha",
                "DMS-1025 shared query description",
                "2026-05-15",
                "2026-12-31"
            ),
            new(
                queryNamespace,
                "Beta",
                "DMS-1025 Query Beta",
                "DMS-1025 shared query description",
                "2026-05-16",
                "2027-01-01"
            ),
            new(
                queryNamespace,
                "Gamma",
                "DMS-1025 Query Gamma",
                "DMS-1025 distinct query description",
                "2026-05-17",
                "2027-01-02"
            ),
        ];

        CreatedDescriptor[] created =
        [
            await CreateDescriptorAsync(harness, payloads[0]),
            await CreateDescriptorAsync(harness, payloads[1]),
            await CreateDescriptorAsync(harness, payloads[2]),
        ];

        string namespaceQuery = $"{DescriptorEndpoint}?namespace={Uri.EscapeDataString(queryNamespace)}";
        using HttpResponseMessage namespaceResponse = await harness.HttpClient.GetAsync(namespaceQuery);
        string namespaceBody = await namespaceResponse.Content.ReadAsStringAsync();
        namespaceResponse.StatusCode.Should().Be(HttpStatusCode.OK, namespaceBody);
        JsonArray namespaceResults = ParseArray(namespaceBody);
        ReadIds(namespaceResults).Should().Equal(created.Select(static descriptor => descriptor.Id));

        string codeValueQuery =
            $"{DescriptorEndpoint}?namespace={Uri.EscapeDataString(queryNamespace)}&codeValue={Uri.EscapeDataString("Beta")}";
        using HttpResponseMessage codeValueResponse = await harness.HttpClient.GetAsync(codeValueQuery);
        string codeValueBody = await codeValueResponse.Content.ReadAsStringAsync();
        codeValueResponse.StatusCode.Should().Be(HttpStatusCode.OK, codeValueBody);
        JsonArray codeValueResults = ParseArray(codeValueBody);
        ReadIds(codeValueResults).Should().Equal(created[1].Id);

        using HttpResponseMessage firstPageResponse = await harness.HttpClient.GetAsync(
            $"{namespaceQuery}&offset=0&limit=2"
        );
        string firstPageBody = await firstPageResponse.Content.ReadAsStringAsync();
        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK, firstPageBody);
        string[] firstPageIds = ReadIds(ParseArray(firstPageBody));

        using HttpResponseMessage secondPageResponse = await harness.HttpClient.GetAsync(
            $"{namespaceQuery}&offset=2&limit=2"
        );
        string secondPageBody = await secondPageResponse.Content.ReadAsStringAsync();
        secondPageResponse.StatusCode.Should().Be(HttpStatusCode.OK, secondPageBody);
        string[] secondPageIds = ReadIds(ParseArray(secondPageBody));

        firstPageIds
            .Should()
            .Equal(created[0].Id, created[1].Id, "descriptor queries currently order by DocumentId ASC");
        secondPageIds.Should().Equal(created[2].Id);
        firstPageIds.Intersect(secondPageIds).Should().BeEmpty();
        firstPageIds.Concat(secondPageIds).Should().BeEquivalentTo(created.Select(static d => d.Id));
    }

    public static async Task It_requires_descriptor_reference_resolution_before_resource_write(
        ApiIntegrationHarness harness
    )
    {
        const string codeValue = "DMS-1025 Reference";
        string descriptorUri = $"{DescriptorNamespace}#{codeValue}";
        JsonObject itemPayload = CreateProfileRootOnlyMergeItem(1025001, descriptorUri);

        using HttpResponseMessage missingResponse = await PostJsonAsync(
            harness,
            ProfileRootOnlyMergeItemsEndpoint,
            itemPayload
        );
        string missingBody = await missingResponse.Content.ReadAsStringAsync();

        missingResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, missingBody);
        JsonObject missingProblem = ParseObject(missingBody);
        GetRequiredString(missingProblem, "type").Should().Be("urn:ed-fi:api:bad-request");
        missingBody.Should().Contain("primarySchoolTypeDescriptor");
        missingBody.ToLowerInvariant().Should().Contain(descriptorUri.ToLowerInvariant());

        await CreateDescriptorAsync(
            harness,
            new DescriptorPayload(
                DescriptorNamespace,
                codeValue,
                "DMS-1025 Reference Short",
                "DMS-1025 Reference Description",
                "2026-05-15",
                "2026-12-31"
            )
        );

        using HttpResponseMessage successResponse = await PostJsonAsync(
            harness,
            ProfileRootOnlyMergeItemsEndpoint,
            CreateProfileRootOnlyMergeItem(1025001, descriptorUri)
        );
        string successBody = await successResponse.Content.ReadAsStringAsync();

        successResponse.StatusCode.Should().Be(HttpStatusCode.Created, successBody);
        successResponse.Headers.Location.Should().NotBeNull();
    }

    private static async Task<CreatedDescriptor> CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        DescriptorPayload payload
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(harness, DescriptorEndpoint, payload.ToJsonObject());
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        response.Headers.Location.Should().NotBeNull();
        response.TryReadRawEtag(out string etag).Should().BeTrue("descriptor POST must emit an ETag");

        string locationPath = ToPath(response.Headers.Location!);
        string id = locationPath.Split('/')[^1];

        return new CreatedDescriptor(locationPath, id, etag, payload);
    }

    private static async Task<HttpResponseMessage> PutDescriptorAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        string id,
        DescriptorPayload payload,
        string ifMatch
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = CreateJsonContent(payload.ToJsonObject(id)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = CreateJsonContent(payload);
        return await harness.HttpClient.PostAsync(endpoint, content);
    }

    private static async Task<JsonObject> GetDescriptorAsync(ApiIntegrationHarness harness, string locationPath)
    {
        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string body = await getResponse.Content.ReadAsStringAsync();

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return ParseObject(body);
    }

    private static async Task<DocumentMetadata> ReadDocumentMetadataAsync(
        ApiIntegrationHarness harness,
        string documentUuid
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT "ContentVersion", "ContentLastModifiedAt"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid
            """;

        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@documentUuid";
        parameter.Value = Guid.Parse(documentUuid);
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"document {documentUuid} should exist");

        long contentVersion = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        DateTime contentLastModifiedAt = reader.GetDateTime(1);

        (await reader.ReadAsync()).Should().BeFalse($"document {documentUuid} should be unique");

        return new DocumentMetadata(contentVersion, contentLastModifiedAt);
    }

    private static JsonObject CreateProfileRootOnlyMergeItem(int id, string descriptorUri)
    {
        return new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = id,
            ["displayName"] = $"Descriptor runtime item {id}",
            ["primarySchoolTypeDescriptor"] = descriptorUri,
            ["secondarySchoolTypeDescriptor"] = descriptorUri,
        };
    }

    private static StringContent CreateJsonContent(JsonObject payload) =>
        new(payload.ToJsonString(), Encoding.UTF8, "application/json");

    private static string ToPath(Uri location) =>
        location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

    private static JsonObject ParseObject(string body)
    {
        JsonNode? node = JsonNode.Parse(body);
        node.Should().NotBeNull("response body must be a JSON document");
        return node!.AsObject();
    }

    private static JsonArray ParseArray(string body)
    {
        JsonNode? node = JsonNode.Parse(body);
        node.Should().NotBeNull("response body must be a JSON document");
        return node!.AsArray();
    }

    private static string GetRequiredString(JsonObject json, string propertyName) =>
        json[propertyName]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Expected JSON string property '{propertyName}'.");

    private static string[] ReadIds(JsonArray array) =>
        [.. array.Select(static node => node!.AsObject()["id"]!.GetValue<string>())];

    private static void AssertDescriptorFields(JsonObject actual, DescriptorPayload expected, string expectedId)
    {
        GetRequiredString(actual, "id").Should().Be(expectedId);
        GetRequiredString(actual, "namespace").Should().Be(expected.Namespace);
        GetRequiredString(actual, "codeValue").Should().Be(expected.CodeValue);
        GetRequiredString(actual, "shortDescription").Should().Be(expected.ShortDescription);

        if (expected.Description is not null)
        {
            GetRequiredString(actual, "description").Should().Be(expected.Description);
        }
        else
        {
            actual.ContainsKey("description").Should().BeFalse();
        }

        if (expected.EffectiveBeginDate is not null)
        {
            GetRequiredString(actual, "effectiveBeginDate").Should().Be(expected.EffectiveBeginDate);
        }
        else
        {
            actual.ContainsKey("effectiveBeginDate").Should().BeFalse();
        }

        if (expected.EffectiveEndDate is not null)
        {
            GetRequiredString(actual, "effectiveEndDate").Should().Be(expected.EffectiveEndDate);
        }
        else
        {
            actual.ContainsKey("effectiveEndDate").Should().BeFalse();
        }
    }

    private sealed record DescriptorPayload(
        string Namespace,
        string CodeValue,
        string ShortDescription,
        string? Description = null,
        string? EffectiveBeginDate = null,
        string? EffectiveEndDate = null
    )
    {
        public JsonObject ToJsonObject(string? id = null)
        {
            var payload = new JsonObject
            {
                ["namespace"] = Namespace,
                ["codeValue"] = CodeValue,
                ["shortDescription"] = ShortDescription,
            };

            if (id is not null)
            {
                payload["id"] = id;
            }

            if (Description is not null)
            {
                payload["description"] = Description;
            }

            if (EffectiveBeginDate is not null)
            {
                payload["effectiveBeginDate"] = EffectiveBeginDate;
            }

            if (EffectiveEndDate is not null)
            {
                payload["effectiveEndDate"] = EffectiveEndDate;
            }

            return payload;
        }
    }

    private sealed record CreatedDescriptor(
        string LocationPath,
        string Id,
        string Etag,
        DescriptorPayload Payload
    );

    private sealed record DocumentMetadata(long ContentVersion, DateTime ContentLastModifiedAt);
}
```

- [ ] **Step 2: Create the PostgreSQL wrapper**

Create `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_DescriptorRuntime.cs` with this content:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_DescriptorRuntime : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    [Test]
    public Task It_creates_and_reads_a_descriptor() =>
        DescriptorRuntimeScenario.It_creates_and_reads_a_descriptor(Harness);

    [Test]
    public Task It_updates_descriptor_non_identity_fields_and_advances_metadata() =>
        DescriptorRuntimeScenario.It_updates_descriptor_non_identity_fields_and_advances_metadata(Harness);

    [Test]
    public Task It_preserves_metadata_for_unchanged_descriptor_put() =>
        DescriptorRuntimeScenario.It_preserves_metadata_for_unchanged_descriptor_put(Harness);

    [Test]
    public Task It_rejects_descriptor_identity_changes() =>
        DescriptorRuntimeScenario.It_rejects_descriptor_identity_changes(Harness);

    [Test]
    public Task It_filters_and_pages_descriptor_queries() =>
        DescriptorRuntimeScenario.It_filters_and_pages_descriptor_queries(Harness);

    [Test]
    public Task It_requires_descriptor_reference_resolution_before_resource_write() =>
        DescriptorRuntimeScenario.It_requires_descriptor_reference_resolution_before_resource_write(Harness);
}
```

- [ ] **Step 3: Create the SQL Server wrapper**

Create `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_DescriptorRuntime.cs` with this content:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_DescriptorRuntime : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    [Test]
    public Task It_creates_and_reads_a_descriptor() =>
        DescriptorRuntimeScenario.It_creates_and_reads_a_descriptor(Harness);

    [Test]
    public Task It_updates_descriptor_non_identity_fields_and_advances_metadata() =>
        DescriptorRuntimeScenario.It_updates_descriptor_non_identity_fields_and_advances_metadata(Harness);

    [Test]
    public Task It_preserves_metadata_for_unchanged_descriptor_put() =>
        DescriptorRuntimeScenario.It_preserves_metadata_for_unchanged_descriptor_put(Harness);

    [Test]
    public Task It_rejects_descriptor_identity_changes() =>
        DescriptorRuntimeScenario.It_rejects_descriptor_identity_changes(Harness);

    [Test]
    public Task It_filters_and_pages_descriptor_queries() =>
        DescriptorRuntimeScenario.It_filters_and_pages_descriptor_queries(Harness);

    [Test]
    public Task It_requires_descriptor_reference_resolution_before_resource_write() =>
        DescriptorRuntimeScenario.It_requires_descriptor_reference_resolution_before_resource_write(Harness);
}
```

- [ ] **Step 4: Format the new C# files**

Run:

```powershell
dotnet csharpier format src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_DescriptorRuntime.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_DescriptorRuntime.cs
```

Expected: command exits `0`.

- [ ] **Step 5: Run descriptor runtime integration tests**

Run:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "FullyQualifiedName~DescriptorRuntime"
```

Expected with configured databases: all descriptor runtime tests pass for configured dialects.

Expected without configured databases: the existing dialect base classes skip cleanly with `Assert.Ignore` messages for missing PostgreSQL or SQL Server connection strings.

If a configured dialect fails, inspect the failure before editing production code. DMS-1025 is a coverage ticket; fix production behavior only when the failure shows the existing runtime violates the DMS-1025 design acceptance criteria.

- [ ] **Step 6: Run a dialect-specific command when only one database is configured**

For PostgreSQL-only environments, run:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "FullyQualifiedName~Given_Postgresql_DescriptorRuntime"
```

Expected: PostgreSQL descriptor runtime tests pass, or skip only when `ConnectionStrings__DatabaseConnection` is not configured.

For SQL Server-only environments, run:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "FullyQualifiedName~Given_Mssql_DescriptorRuntime"
```

Expected: SQL Server descriptor runtime tests pass, or skip only when `ConnectionStrings__MssqlAdmin` is not configured.

- [ ] **Step 7: Commit scenario coverage**

```powershell
git add src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_DescriptorRuntime.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_DescriptorRuntime.cs
git commit -m "test: cover descriptor runtime behavior"
```

---

### Task 3: Update Test Documentation And Run Final Verification

**Files:**
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md`

- [ ] **Step 1: Update the fixture map**

In `src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md`, add this row to the fixture map after `ProfileRootOnlyMerge`:

```markdown
| `DescriptorRuntime` | `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/` | Descriptor runtime CRUD, metadata, query, paging, and descriptor-reference coverage with the full shared descriptor query contract. |
```

- [ ] **Step 2: Verify there is still no seed-descriptors implementation in this branch**

Run:

```powershell
rg -n -S "seed-descriptors|SeedDescriptors|InterchangeDescriptors" src/dms/clis src/dms/tests/EdFi.DataManagementService.Tests.Integration
```

Expected: no `ddl provision --seed-descriptors` command implementation or API integration seeding test exists in these paths. References in design docs or unrelated backend test support do not make seeding in scope for DMS-1025.

- [ ] **Step 3: Format all touched C# one more time**

Run:

```powershell
dotnet csharpier format src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_DescriptorRuntime.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_DescriptorRuntime.cs
```

Expected: command exits `0`.

- [ ] **Step 4: Run the final descriptor runtime test filter**

Run:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "FullyQualifiedName~DescriptorRuntime"
```

Expected: all descriptor runtime tests pass for configured dialects; unconfigured dialects skip cleanly.

- [ ] **Step 5: Run a broader integration compile/test check**

Run:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --no-restore
```

Expected: the integration project compiles, configured tests pass, and unconfigured dialect suites skip cleanly.

- [ ] **Step 6: Commit documentation and verification cleanup**

```powershell
git add src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md
git commit -m "docs: document descriptor runtime fixture"
```

If formatting changed files from earlier tasks, include those formatted files in this commit too:

```powershell
git add src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_DescriptorRuntime.cs src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_DescriptorRuntime.cs
git commit -m "style: format descriptor runtime tests"
```

Only run the second commit command when `git status --short` shows formatting changes in those C# files after the docs commit.

---

## Self-Review Checklist

- Spec coverage:
  - POST create/read is covered by `It_creates_and_reads_a_descriptor`.
  - PUT changed non-identity fields and metadata advancement is covered by `It_updates_descriptor_non_identity_fields_and_advances_metadata`.
  - No-op PUT metadata preservation is covered by `It_preserves_metadata_for_unchanged_descriptor_put`.
  - Identity immutability is covered by `It_rejects_descriptor_identity_changes`.
  - Descriptor query filtering and paging is covered by `It_filters_and_pages_descriptor_queries`.
  - Descriptor-reference failure and API-created success is covered by `It_requires_descriptor_reference_resolution_before_resource_write`, with success asserted as `201 Created`.
  - PostgreSQL and SQL Server parity is covered by thin wrappers calling the same scenario methods.
  - Descriptor seeding is explicitly deferred to `DMS-955`.
- Type consistency:
  - `FixtureKey.DescriptorRuntime` is used by both wrappers and mapped in `FixtureRepositoryPaths`.
  - `DescriptorRuntimeScenario` methods exactly match wrapper method calls.
  - The fixture query mapping exactly matches the seven-field `DescriptorQueryCapabilityCompiler` contract.
- Verification:
  - Run the targeted descriptor runtime filter.
  - Run the integration project compile/test command.
  - Run `dotnet csharpier format` on touched C# files.
