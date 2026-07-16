// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Provider-agnostic scenario data and assertion helpers for the no-profile full-surface collection
/// reorder (`FullSurfaceCollectionReorder`). A changed PUT that reorders a base collection by semantic
/// identity must reuse each stored base CollectionItemId, recompute contiguous 0-based ordinals, keep
/// the collection-aligned extension rows keyed to the original base ids (not the request ordinal), and
/// preserve the root row while bumping ContentVersion. Each provider suite keeps its own provisioning,
/// resolver registration, no-profile merge production-boundary invocation, dialect SQL, and readback,
/// but consumes the shared request bodies, document-info builder, neutral persisted-state snapshot
/// records, and FluentAssertions helpers defined here. The internal Core upsert/update request types
/// are constructed in each provider adapter.
/// </summary>
public static class NoProfileCollectionReorderScenarios
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    public static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000001")
    );

    /// <summary>Create body: a School with two addresses (Austin, Dallas) and two collection-aligned extension addresses (Zone-1, Zone-2) keyed to those base addresses.</summary>
    public const string CreateRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Austin"
            },
            {
              "city": "Dallas"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    /// <summary>Changed PUT body: the same rows reordered (Dallas, Austin) with the aligned extension addresses moved with their base addresses (Zone-2, Zone-1).</summary>
    public const string UpdateRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Dallas"
            },
            {
              "city": "Austin"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    public static JsonNode CreateRequestBody() => JsonNode.Parse(CreateRequestBodyJson)!;

    public static JsonNode UpdateRequestBody() => JsonNode.Parse(UpdateRequestBodyJson)!;

    /// <summary>Builds the provider-neutral School document info (School identity 255901).</summary>
    public static DocumentInfo CreateSchoolDocumentInfo()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    public sealed record DocumentRow(
        long DocumentId,
        Guid DocumentUuid,
        short ResourceKeyId,
        long ContentVersion
    );

    public sealed record SchoolRow(long DocumentId, long SchoolId, string? ShortName);

    public sealed record SchoolAddressRow(
        long CollectionItemId,
        long SchoolDocumentId,
        int Ordinal,
        string City
    );

    public sealed record SchoolExtensionAddressRow(
        long BaseCollectionItemId,
        long SchoolDocumentId,
        string Zone
    );

    public sealed record PersistedState(
        DocumentRow Document,
        SchoolRow School,
        IReadOnlyList<SchoolAddressRow> Addresses,
        IReadOnlyList<SchoolExtensionAddressRow> ExtensionAddresses
    );

    /// <summary>Asserts the full-surface reorder returned UpdateSuccess for the existing document, kept its identity, and bumped ContentVersion.</summary>
    public static void AssertUpdateSuccessAndContentVersionBump(
        UpdateResult result,
        MappingSet mappingSet,
        PersistedState before,
        PersistedState after
    )
    {
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        result.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
        after.Document.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        after.Document.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[SchoolResource]);
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
    }

    /// <summary>
    /// Asserts the reorder matched stored rows by semantic identity: each base address kept its
    /// original CollectionItemId (compared before-to-after) while ordinals were recomputed to the new
    /// request order, the root row was preserved, and the collection-aligned extension rows stayed
    /// keyed to their original base CollectionItemIds rather than the request ordinal.
    /// </summary>
    public static void AssertReusesCollectionItemIdsWhileRecomputingOrdinals(
        PersistedState before,
        PersistedState after
    )
    {
        before.Addresses.Should().HaveCount(2);

        after.School.Should().Be(new SchoolRow(after.Document.DocumentId, 255901, "LHS"));

        after
            .Addresses.Should()
            .Equal(
                new SchoolAddressRow(
                    before.Addresses[1].CollectionItemId,
                    after.Document.DocumentId,
                    0,
                    "Dallas"
                ),
                new SchoolAddressRow(
                    before.Addresses[0].CollectionItemId,
                    after.Document.DocumentId,
                    1,
                    "Austin"
                )
            );

        after
            .ExtensionAddresses.Should()
            .Equal(
                new SchoolExtensionAddressRow(
                    before.Addresses[0].CollectionItemId,
                    after.Document.DocumentId,
                    "Zone-1"
                ),
                new SchoolExtensionAddressRow(
                    before.Addresses[1].CollectionItemId,
                    after.Document.DocumentId,
                    "Zone-2"
                )
            );
    }

    /// <summary>Asserts the two-row ordinal swap committed under the database sibling-ordinal uniqueness constraint, leaving exactly contiguous, unique ordinals 0,1 in the new order.</summary>
    public static void AssertTwoRowSwapCommitsUnderSiblingUniqueness(
        UpdateResult result,
        PersistedState after
    )
    {
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        after.Addresses.Should().HaveCount(2);
        after.Addresses.Select(static row => row.Ordinal).Should().Equal(0, 1);
        after.Addresses.Select(static row => row.Ordinal).Should().OnlyHaveUniqueItems();
        after.Addresses.Select(static row => row.City).Should().Equal("Dallas", "Austin");
    }
}
