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

    public sealed record ExtensionChildRootExtensionRow(long DocumentId, string CampusCode);

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
    /// intervention and visit rows while leaving the root School, its base address row, and the root
    /// extension row intact (same DocumentId, same base address CollectionItemId, same retained
    /// campusCode — not delete-and-reinserted and not deleted along with its child collection).
    /// </summary>
    public static void AssertStandaloneExtensionChildCollectionDeleted(
        UpdateResult result,
        ExtensionChildSchoolRow schoolBefore,
        ExtensionChildSchoolRow schoolAfter,
        ExtensionChildRootExtensionRow extensionBefore,
        ExtensionChildRootExtensionRow extensionAfter,
        IReadOnlyList<ExtensionChildAddressRow> addressesBefore,
        IReadOnlyList<ExtensionChildAddressRow> addressesAfter,
        IReadOnlyList<ExtensionInterventionRow> interventionsBefore,
        IReadOnlyList<ExtensionInterventionVisitRow> visitsBefore,
        IReadOnlyList<ExtensionInterventionRow> interventionsAfter,
        IReadOnlyList<ExtensionInterventionVisitRow> visitsAfter
    )
    {
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();

        // Preconditions: the standalone extension-child collection, one base address, and the root
        // extension row all exist, so the survival assertions below cannot pass vacuously.
        schoolBefore.SchoolId.Should().Be(255901);
        interventionsBefore.Should().HaveCount(2);
        visitsBefore.Should().HaveCount(3);
        addressesBefore
            .Should()
            .ContainSingle("the create persists exactly one base address")
            .Which.City.Should()
            .Be("Austin");
        extensionBefore
            .Should()
            .Be(
                new ExtensionChildRootExtensionRow(schoolBefore.DocumentId, "North"),
                "the create persists the root extension row the update body retains"
            );

        interventionsAfter.Should().BeEmpty("the omitted standalone extension-child collection is deleted");
        visitsAfter.Should().BeEmpty("deleting the intervention rows deletes their child visit rows");

        // The root, base address, and root extension rows survive unchanged: same DocumentId/SchoolId,
        // the same base address CollectionItemId/parent/ordinal/value, and the same retained
        // campusCode (deleting the whole root extension along with its child collection would fail
        // this, as would a delete-and-reinsert).
        schoolAfter.Should().Be(schoolBefore, "the root School row keeps its DocumentId and SchoolId");
        extensionAfter
            .Should()
            .Be(extensionBefore, "the retained root extension row survives the child-collection delete");
        addressesAfter
            .Should()
            .Equal(
                new ExtensionChildAddressRow(
                    addressesBefore[0].CollectionItemId,
                    schoolBefore.DocumentId,
                    0,
                    "Austin"
                )
            );
    }

    // --- DeletedBaseCollectionRows (multi-batch base collection delete) -----------------------

    /// <summary>
    /// Asserts a changed PUT that retains a single base collection row deleted the omitted rows (the
    /// create must exceed the compiled batch limit) and left exactly the retained row with its stable
    /// CollectionItemId, parent document id, ordinal 0, and value. Batch-partitioning mechanics stay
    /// in the multi-batch suite.
    /// </summary>
    public static void AssertMultiBatchBaseCollectionReducedToRetainedRow(
        UpdateResult result,
        DocumentUuid documentUuid,
        long documentId,
        int maxRowsPerBatch,
        IReadOnlyList<SchoolAddressRow> addressesBefore,
        IReadOnlyList<SchoolAddressRow> addressesAfter,
        string retainedCity
    )
    {
        addressesBefore
            .Count.Should()
            .BeGreaterThan(maxRowsPerBatch, "the create must exceed the compiled batch limit");

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        result.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(documentUuid);

        addressesAfter.Should().ContainSingle("the changed PUT retains exactly one base collection row");
        addressesAfter[0]
            .Should()
            .Be(new SchoolAddressRow(addressesBefore[0].CollectionItemId, documentId, 0, retainedCity));
    }

    // --- DeletedAndReplacedChildCollectionRows (authoritative child-collection replace) --------

    /// <summary>
    /// Asserts a changed POST-as-update that retained the first child row and replaced the second
    /// reused the retained row's stable CollectionItemId, dropped the omitted prior row's id, and
    /// assigned the replacement row a new id. The detailed row/value/FK assertions stay in the
    /// authoritative-resource suite.
    /// </summary>
    public static void AssertRetainedChildRowIdStableAndOmittedRowReplaced(
        string collectionName,
        IReadOnlyList<long> collectionItemIdsBefore,
        IReadOnlyList<long> collectionItemIdsAfter
    )
    {
        collectionItemIdsBefore
            .Should()
            .HaveCount(2, "{0} starts with two rows before the changed POST-as-update", collectionName);
        collectionItemIdsAfter
            .Should()
            .HaveCount(2, "{0} keeps two rows after the changed POST-as-update", collectionName);
        collectionItemIdsAfter[0]
            .Should()
            .Be(
                collectionItemIdsBefore[0],
                "{0} retained row keeps its stable CollectionItemId",
                collectionName
            );
        collectionItemIdsAfter[1]
            .Should()
            .NotBe(
                collectionItemIdsBefore[1],
                "{0} replacement row is assigned a new CollectionItemId",
                collectionName
            );
        collectionItemIdsAfter
            .Should()
            .NotContain(
                collectionItemIdsBefore[1],
                "{0} omitted prior row id no longer exists",
                collectionName
            );
    }
}
