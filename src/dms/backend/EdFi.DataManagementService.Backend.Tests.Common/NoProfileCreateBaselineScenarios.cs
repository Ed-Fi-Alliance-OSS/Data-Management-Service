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
/// Provider-agnostic scenario data and assertion helpers for the no-profile full-surface create
/// baseline (`NoProfileFullSurfaceCreate`). Each provider suite keeps its own database provisioning,
/// resolver registration, no-profile production-boundary invocation, SQL dialect, and readback, but
/// consumes the shared request builder, the neutral persisted-state snapshot records, and the shared
/// FluentAssertions helpers defined here so PostgreSQL and (later) SQL Server share a single semantic
/// source of truth for what a full-surface create must persist.
/// </summary>
public static class NoProfileCreateBaselineScenarios
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    public static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")
    );

    /// <summary>
    /// The full-surface create request body: a School with a nested address/period collection and a
    /// `sample` extension carrying a scalar, a collection-aligned extension address collection, and an
    /// extension-child intervention/visit collection.
    /// </summary>
    public const string RequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin",
              "periods": [
                {
                  "periodName": "Morning"
                },
                {
                  "periodName": "Afternoon"
                }
              ]
            },
            {
              "city": "Dallas",
              "periods": [
                {
                  "periodName": "Evening"
                }
              ]
            }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North",
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
              ],
              "interventions": [
                {
                  "interventionCode": "Attendance",
                  "visits": [
                    {
                      "visitCode": "Visit-A"
                    },
                    {
                      "visitCode": "Visit-B"
                    }
                  ]
                },
                {
                  "interventionCode": "Behavior",
                  "visits": [
                    {
                      "visitCode": "Visit-C"
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

    /// <summary>Parses the shared full-surface create request body into a JSON node.</summary>
    public static JsonNode CreateRequestBody() => JsonNode.Parse(RequestBodyJson)!;

    /// <summary>
    /// Builds the provider-neutral School document info for the create request. The provider adapter
    /// wraps this in its own upsert request because that request type is internal to the Core
    /// assembly and only visible to the provider test projects.
    /// </summary>
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

    // Provider-neutral persisted-state snapshot records. Each provider suite reads back its own SQL
    // into these shapes and hands them to the assertion helpers below.

    public sealed record PersistedDocumentRow(
        long DocumentId,
        Guid DocumentUuid,
        short ResourceKeyId,
        long ContentVersion
    );

    public sealed record PersistedSchoolRow(long DocumentId, long SchoolId);

    public sealed record PersistedSchoolAddressRow(
        long CollectionItemId,
        long SchoolDocumentId,
        int Ordinal,
        string City
    );

    public sealed record PersistedSchoolAddressPeriodRow(
        long CollectionItemId,
        long SchoolDocumentId,
        long ParentCollectionItemId,
        int Ordinal,
        string PeriodName
    );

    public sealed record PersistedSchoolExtensionRow(long DocumentId, string CampusCode);

    public sealed record PersistedSchoolExtensionAddressRow(
        long BaseCollectionItemId,
        long SchoolDocumentId,
        string Zone
    );

    public sealed record PersistedSchoolExtensionInterventionRow(
        long CollectionItemId,
        long SchoolDocumentId,
        int Ordinal,
        string InterventionCode
    );

    public sealed record PersistedSchoolExtensionInterventionVisitRow(
        long CollectionItemId,
        long SchoolDocumentId,
        long ParentCollectionItemId,
        int Ordinal,
        string VisitCode
    );

    /// <summary>Asserts the create returned InsertSuccess and stamped the expected Document row.</summary>
    public static void AssertInsertSuccess(
        UpsertResult result,
        MappingSet mappingSet,
        PersistedDocumentRow document
    )
    {
        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        result.As<UpsertResult.InsertSuccess>().NewDocumentUuid.Should().Be(SchoolDocumentUuid);
        document.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        document.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[SchoolResource]);
        document.ContentVersion.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Asserts the root row and the nested address/period collection persisted with stable, unique,
    /// positive CollectionItemIds and contiguous 0-based ordinals aligned to their parents.
    /// </summary>
    public static void AssertRootAndNestedCollectionRows(
        PersistedDocumentRow document,
        PersistedSchoolRow school,
        IReadOnlyList<PersistedSchoolAddressRow> addresses,
        IReadOnlyList<PersistedSchoolAddressPeriodRow> addressPeriods
    )
    {
        school.DocumentId.Should().Be(document.DocumentId);
        school.SchoolId.Should().Be(255901);

        addresses
            .Should()
            .Equal(
                new PersistedSchoolAddressRow(
                    addresses[0].CollectionItemId,
                    document.DocumentId,
                    0,
                    "Austin"
                ),
                new PersistedSchoolAddressRow(addresses[1].CollectionItemId, document.DocumentId, 1, "Dallas")
            );
        addresses.Select(static row => row.CollectionItemId).Should().OnlyHaveUniqueItems();
        addresses.Select(static row => row.CollectionItemId).Should().OnlyContain(id => id > 0);

        addressPeriods
            .Should()
            .Equal(
                new PersistedSchoolAddressPeriodRow(
                    addressPeriods[0].CollectionItemId,
                    document.DocumentId,
                    addresses[0].CollectionItemId,
                    0,
                    "Morning"
                ),
                new PersistedSchoolAddressPeriodRow(
                    addressPeriods[1].CollectionItemId,
                    document.DocumentId,
                    addresses[0].CollectionItemId,
                    1,
                    "Afternoon"
                ),
                new PersistedSchoolAddressPeriodRow(
                    addressPeriods[2].CollectionItemId,
                    document.DocumentId,
                    addresses[1].CollectionItemId,
                    0,
                    "Evening"
                )
            );
        addressPeriods.Select(static row => row.CollectionItemId).Should().OnlyHaveUniqueItems();
        addressPeriods.Select(static row => row.CollectionItemId).Should().OnlyContain(id => id > 0);
    }

    /// <summary>
    /// Asserts the root extension, the collection-aligned extension addresses (keyed to the base
    /// address CollectionItemIds), and the extension-child intervention/visit collection persisted.
    /// </summary>
    public static void AssertRootAndCollectionExtensionAndExtensionChildRows(
        PersistedDocumentRow document,
        IReadOnlyList<PersistedSchoolAddressRow> addresses,
        PersistedSchoolExtensionRow extension,
        IReadOnlyList<PersistedSchoolExtensionAddressRow> extensionAddresses,
        IReadOnlyList<PersistedSchoolExtensionInterventionRow> interventions,
        IReadOnlyList<PersistedSchoolExtensionInterventionVisitRow> interventionVisits
    )
    {
        extension.Should().Be(new PersistedSchoolExtensionRow(document.DocumentId, "North"));

        extensionAddresses
            .Should()
            .Equal(
                new PersistedSchoolExtensionAddressRow(
                    addresses[0].CollectionItemId,
                    document.DocumentId,
                    "Zone-1"
                ),
                new PersistedSchoolExtensionAddressRow(
                    addresses[1].CollectionItemId,
                    document.DocumentId,
                    "Zone-2"
                )
            );

        interventions
            .Should()
            .Equal(
                new PersistedSchoolExtensionInterventionRow(
                    interventions[0].CollectionItemId,
                    document.DocumentId,
                    0,
                    "Attendance"
                ),
                new PersistedSchoolExtensionInterventionRow(
                    interventions[1].CollectionItemId,
                    document.DocumentId,
                    1,
                    "Behavior"
                )
            );
        interventions.Select(static row => row.CollectionItemId).Should().OnlyHaveUniqueItems();
        interventions.Select(static row => row.CollectionItemId).Should().OnlyContain(id => id > 0);

        interventionVisits.Should().HaveCount(3);
        interventionVisits
            .Should()
            .Equal(
                new PersistedSchoolExtensionInterventionVisitRow(
                    interventionVisits[0].CollectionItemId,
                    document.DocumentId,
                    interventions[0].CollectionItemId,
                    0,
                    "Visit-A"
                ),
                new PersistedSchoolExtensionInterventionVisitRow(
                    interventionVisits[1].CollectionItemId,
                    document.DocumentId,
                    interventions[0].CollectionItemId,
                    1,
                    "Visit-B"
                ),
                new PersistedSchoolExtensionInterventionVisitRow(
                    interventionVisits[2].CollectionItemId,
                    document.DocumentId,
                    interventions[1].CollectionItemId,
                    0,
                    "Visit-C"
                )
            );
        interventionVisits.Select(static row => row.CollectionItemId).Should().OnlyHaveUniqueItems();
        interventionVisits.Select(static row => row.CollectionItemId).Should().OnlyContain(id => id > 0);
    }

    /// <summary>
    /// Asserts the created full-surface document reconstitutes correctly through the production
    /// relational GET-by-id read path: GetSuccess for the created document, the stored last-modified
    /// stamp served as metadata, a well-formed composed ETag with write/read ETag parity, and
    /// canonical semantic-JSON equality between the served document and the full-surface request
    /// body (root scalar, nested address/period collections, root extension, collection-aligned
    /// extension addresses, and extension-child intervention/visit collections). The write path
    /// accepts the collection-aligned extension zones as a parallel array under the root _ext, while
    /// the read path serves them in the canonical per-item shape (each base address carries its own
    /// _ext), so the expected document is reshaped accordingly before comparison.
    /// </summary>
    public static void AssertFullSurfaceDocumentReconstitutes(
        UpsertResult createResult,
        GetResult getResult,
        MappingSet mappingSet,
        DateTimeOffset expectedLastModifiedAt,
        long expectedContentVersion
    )
    {
        getResult.Should().BeOfType<GetResult.GetSuccess>();

        var success = (GetResult.GetSuccess)getResult;

        success.DocumentUuid.Should().Be(SchoolDocumentUuid);
        success.LastModifiedTraceId.Should().BeNull();
        success.LastModifiedDate.Should().Be(expectedLastModifiedAt.UtcDateTime);
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(SchoolDocumentUuid.Value.ToString());
        RelationalGetIntegrationTestHelper.AssertComposedEtagServesContentVersion(
            success.EdfiDoc["_etag"]!.GetValue<string>(),
            expectedContentVersion
        );
        success.EdfiDoc["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(RelationalGetIntegrationTestHelper.FormatExternalLastModifiedDate(expectedLastModifiedAt));
        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(createResult, getResult);

        var expectedDocument = RelationalGetIntegrationTestHelper.CreateExpectedExternalResponse(
            RequestBodyJson,
            SchoolResourceInfo,
            mappingSet,
            SchoolDocumentUuid.Value,
            expectedLastModifiedAt
        );

        ReshapeAlignedExtensionAddressesToServedForm(expectedDocument);

        RelationalGetIntegrationTestHelper
            .CanonicalizeJson(success.EdfiDoc)
            .Should()
            .Be(RelationalGetIntegrationTestHelper.CanonicalizeJson(expectedDocument));
    }

    // Moves each parallel _ext.sample.addresses[i]._ext onto the matching base addresses[i] and drops
    // the parallel array, producing the canonical served shape for collection-aligned extensions.
    private static void ReshapeAlignedExtensionAddressesToServedForm(JsonObject expectedDocument)
    {
        var expectedExtension = expectedDocument["_ext"]!["sample"]!.AsObject();
        var alignedAddresses = expectedExtension["addresses"]!.AsArray();
        var baseAddresses = expectedDocument["addresses"]!.AsArray();

        alignedAddresses.Should().HaveCount(baseAddresses.Count);

        for (int index = 0; index < baseAddresses.Count; index++)
        {
            baseAddresses[index]!["_ext"] = alignedAddresses[index]!["_ext"]!.DeepClone();
        }

        expectedExtension.Remove("addresses");
    }
}
