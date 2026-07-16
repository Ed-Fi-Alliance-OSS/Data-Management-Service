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
/// Provider-agnostic scenario data and assertion helpers for the no-profile changed-PUT
/// omission/deletion semantics (`NoProfileChangedPutOmissionSemantics`). Each provider suite keeps
/// its own provisioning, resolver registration, no-profile production-boundary invocation, dialect
/// SQL, and readback, but consumes the shared request bodies, document-info builder, neutral
/// persisted-state snapshot records, and FluentAssertions helpers defined here. The internal Core
/// upsert/update request types are constructed in each provider adapter.
/// </summary>
public static class NoProfileUpdateSemanticsScenarios
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
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002")
    );

    /// <summary>Create body: a School with a clearable inlined <c>shortName</c>, two addresses, and two collection-aligned extension addresses.</summary>
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

    /// <summary>Changed PUT body: omits <c>shortName</c> (clears it), updates the first aligned extension address, and omits the second (deletes that aligned extension scope while keeping both base addresses).</summary>
    public const string UpdateRequestBodyJson = """
        {
          "schoolId": 255901,
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
                      "zone": "Zone-1-Updated"
                    }
                  }
                },
                {}
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

    /// <summary>Asserts the changed PUT returned UpdateSuccess for the existing document and bumped ContentVersion.</summary>
    public static void AssertUpdateSuccessAndContentVersionBump(
        UpdateResult result,
        MappingSet mappingSet,
        DocumentRow documentBefore,
        DocumentRow documentAfter
    )
    {
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        result.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
        documentAfter.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        documentAfter.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[SchoolResource]);
        documentAfter.ContentVersion.Should().BeGreaterThan(documentBefore.ContentVersion);
    }

    /// <summary>Asserts the omitted inlined <c>shortName</c> column was cleared to null rather than preserved.</summary>
    public static void AssertClearedOmittedInlinedColumn(DocumentRow documentAfter, SchoolRow schoolAfter) =>
        schoolAfter.Should().Be(new SchoolRow(documentAfter.DocumentId, 255901, null));

    /// <summary>
    /// Asserts the omitted collection-aligned extension scope row was deleted while both base
    /// address rows were retained (stable ids) and the first aligned extension address was updated.
    /// </summary>
    public static void AssertDeletedOmittedAlignedExtensionScope(
        DocumentRow documentAfter,
        IReadOnlyList<SchoolAddressRow> addressesBefore,
        IReadOnlyList<SchoolExtensionAddressRow> extensionAddressesBefore,
        IReadOnlyList<SchoolAddressRow> addressesAfter,
        IReadOnlyList<SchoolExtensionAddressRow> extensionAddressesAfter
    )
    {
        addressesBefore.Should().HaveCount(2);
        extensionAddressesBefore.Should().HaveCount(2);

        addressesAfter
            .Should()
            .Equal(
                new SchoolAddressRow(
                    addressesBefore[0].CollectionItemId,
                    documentAfter.DocumentId,
                    0,
                    "Austin"
                ),
                new SchoolAddressRow(
                    addressesBefore[1].CollectionItemId,
                    documentAfter.DocumentId,
                    1,
                    "Dallas"
                )
            );

        extensionAddressesAfter
            .Should()
            .Equal(
                new SchoolExtensionAddressRow(
                    addressesBefore[0].CollectionItemId,
                    documentAfter.DocumentId,
                    "Zone-1-Updated"
                )
            );
    }

    // --- G1: standalone extension-child collection deletion on changed PUT --------------------
    // Uses the extension-child-collections fixture with a focused body that carries a standalone
    // extension-child intervention/visit collection alongside a preserved root, base address, and
    // root extension. The changed PUT omits only the interventions, which must delete those rows.

    public const string StandaloneExtensionChildFixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    public static readonly DocumentUuid StandaloneExtensionChildDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003")
    );

    /// <summary>Create body for G1: a School with a base address, a root extension scalar, and a standalone extension-child intervention/visit collection.</summary>
    public const string StandaloneExtensionChildCreateBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin"
            }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North",
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

    /// <summary>Changed PUT body for G1: the create body with the standalone extension-child <c>interventions</c> collection omitted (which must delete those rows) while keeping the root, address, and root extension.</summary>
    public const string StandaloneExtensionChildUpdateBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin"
            }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North"
            }
          }
        }
        """;

    public static JsonNode StandaloneExtensionChildCreateBody() =>
        JsonNode.Parse(StandaloneExtensionChildCreateBodyJson)!;

    public static JsonNode StandaloneExtensionChildUpdateBody() =>
        JsonNode.Parse(StandaloneExtensionChildUpdateBodyJson)!;

    public sealed record ExtensionChildSchoolRow(long DocumentId, long SchoolId);

    public sealed record ExtensionChildAddressRow(
        long CollectionItemId,
        long SchoolDocumentId,
        int Ordinal,
        string City
    );

    public sealed record ExtensionInterventionRow(
        long CollectionItemId,
        long SchoolDocumentId,
        int Ordinal,
        string InterventionCode
    );

    public sealed record ExtensionInterventionVisitRow(
        long CollectionItemId,
        long SchoolDocumentId,
        long ParentCollectionItemId,
        int Ordinal,
        string VisitCode
    );

    /// <summary>
    /// Asserts that omitting the standalone extension-child collection on a changed PUT deletes its
    /// intervention and visit rows while leaving the root School and its base address rows intact.
    /// </summary>
    public static void AssertStandaloneExtensionChildCollectionDeleted(
        UpdateResult result,
        ExtensionChildSchoolRow schoolAfter,
        IReadOnlyList<ExtensionChildAddressRow> addressesAfter,
        IReadOnlyList<ExtensionInterventionRow> interventionsBefore,
        IReadOnlyList<ExtensionInterventionVisitRow> visitsBefore,
        IReadOnlyList<ExtensionInterventionRow> interventionsAfter,
        IReadOnlyList<ExtensionInterventionVisitRow> visitsAfter
    )
    {
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();

        interventionsBefore.Should().HaveCount(2);
        visitsBefore.Should().HaveCount(3);

        interventionsAfter.Should().BeEmpty("the omitted standalone extension-child collection is deleted");
        visitsAfter.Should().BeEmpty("deleting the intervention rows deletes their child visit rows");

        schoolAfter.SchoolId.Should().Be(255901, "the root School row is preserved");
        addressesAfter.Should().HaveCount(1, "the base address collection is preserved");
        addressesAfter[0].SchoolDocumentId.Should().Be(schoolAfter.DocumentId);
        addressesAfter[0].Ordinal.Should().Be(0);
        addressesAfter[0].City.Should().Be("Austin");
    }
}
