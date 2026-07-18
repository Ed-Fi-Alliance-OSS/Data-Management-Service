// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Provider-agnostic scenario data and assertion helpers for the no-profile multi-batch collection
/// family (`NoProfileMultiBatchCollection`): collection create/update/delete are partitioned into
/// batches at the compiled MaxRowsPerBatch / ParametersPerRow, and a real authoritative payload
/// exercises genuine parameter pressure. Each provider suite keeps its own provisioning, command
/// recording, dialect SQL/readback, and request execution; it translates its recorded commands into
/// the small provider-neutral batch summaries (reservation row counts and per-command parameter
/// counts) and neutral persisted-state projections defined here, then delegates the behavioral
/// assertions. No provider dialect SQL (for example generate_series or quoted table names) or driver
/// types belong in this contract.
/// </summary>
public static class NoProfileMultiBatchCollectionScenarios
{
    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    /// <summary>Deterministic city value for the address at <paramref name="index"/> (shared by request builders and assertions).</summary>
    public static string CreateCity(int index) =>
        $"City-{index.ToString("D5", CultureInfo.InvariantCulture)}";

    /// <summary>Deterministic zone value for the collection-aligned extension at <paramref name="index"/>.</summary>
    public static string CreateZone(int index) =>
        $"Zone-{index.ToString("D5", CultureInfo.InvariantCulture)}";

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

    /// <summary>A base collection address row plus its resolved AddressType descriptor id, used by the changed-descriptor update-batch scenario.</summary>
    public sealed record SchoolAddressWithDescriptorRow(
        long CollectionItemId,
        long SchoolDocumentId,
        int Ordinal,
        string City,
        long AddressTypeDescriptorId
    );

    /// <summary>
    /// Asserts a multi-batch create (request count exceeding the compiled batch limit) returned
    /// InsertSuccess and persisted the full large base collection with the expected document/root
    /// identity, contiguous 0-based ordinals, unique CollectionItemIds, and correct first/last values.
    /// </summary>
    public static void AssertLargeCollectionCreatePersisted(
        UpsertResult result,
        DocumentUuid documentUuid,
        MappingSet mappingSet,
        int maxRowsPerBatch,
        int requestedAddressCount,
        DocumentRow document,
        SchoolRow school,
        IReadOnlyList<SchoolAddressRow> addresses
    )
    {
        requestedAddressCount
            .Should()
            .BeGreaterThan(maxRowsPerBatch, "the request must exceed the compiled batch limit");

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        result.As<UpsertResult.InsertSuccess>().NewDocumentUuid.Should().Be(documentUuid);

        document.DocumentUuid.Should().Be(documentUuid.Value);
        document.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[SchoolResource]);
        school.Should().Be(new SchoolRow(document.DocumentId, 255901, "BATCH"));

        addresses.Should().HaveCount(requestedAddressCount);
        addresses
            .Select(address => address.Ordinal)
            .Should()
            .Equal(Enumerable.Range(0, requestedAddressCount));
        addresses.Select(address => address.CollectionItemId).Should().OnlyHaveUniqueItems();

        addresses[0]
            .Should()
            .Be(new SchoolAddressRow(addresses[0].CollectionItemId, document.DocumentId, 0, CreateCity(0)));
        addresses[^1]
            .Should()
            .Be(
                new SchoolAddressRow(
                    addresses[^1].CollectionItemId,
                    document.DocumentId,
                    requestedAddressCount - 1,
                    CreateCity(requestedAddressCount - 1)
                )
            );
    }

    /// <summary>
    /// Asserts the id-reservation and base-collection insert commands were partitioned into exactly two
    /// batches at the compiled limit: reservation row counts <c>[maxRowsPerBatch, 2]</c> and insert
    /// parameter counts <c>[maxRowsPerBatch * parametersPerRow, 2 * parametersPerRow]</c>.
    /// </summary>
    public static void AssertCreateBatchPartitions(
        IReadOnlyList<int> reservationRowCounts,
        IReadOnlyList<int> insertParameterCounts,
        int maxRowsPerBatch,
        int parametersPerRow
    )
    {
        reservationRowCounts.Should().Equal(maxRowsPerBatch, 2);
        insertParameterCounts.Should().Equal(maxRowsPerBatch * parametersPerRow, 2 * parametersPerRow);
    }

    /// <summary>
    /// Asserts the base-collection delete commands were partitioned into exactly two batches at the
    /// compiled limit: delete parameter counts <c>[maxRowsPerBatch * parametersPerRow, parametersPerRow]</c>.
    /// </summary>
    public static void AssertDeleteBatchPartitions(
        IReadOnlyList<int> deleteParameterCounts,
        int maxRowsPerBatch,
        int parametersPerRow
    ) => deleteParameterCounts.Should().Equal(maxRowsPerBatch * parametersPerRow, parametersPerRow);

    /// <summary>
    /// Asserts a changed PUT that replaces a non-identity attribute (the AddressType descriptor) on every one
    /// of MaxRowsPerBatch + 2 existing base collection rows, keeping each city and the request order, returns
    /// UpdateSuccess and reduces to a pure collection-update: the full rowset is preserved one-for-one, every
    /// stable CollectionItemId is reused in place (matched by unchanged city/ordinal), the parent id and the
    /// contiguous 0-based ordinals are unchanged, every city is unchanged, and every row's descriptor identity
    /// moved from the original to the replacement. Non-vacuous: the two descriptor identities must differ and
    /// the change must span more than one full update batch.
    /// </summary>
    public static void AssertLargeCollectionChangedDescriptorUpdatePersisted(
        UpdateResult result,
        DocumentUuid documentUuid,
        long documentId,
        int maxRowsPerBatch,
        long originalAddressTypeDescriptorId,
        long replacementAddressTypeDescriptorId,
        IReadOnlyList<SchoolAddressWithDescriptorRow> addressesBefore,
        IReadOnlyList<SchoolAddressWithDescriptorRow> addressesAfter
    )
    {
        originalAddressTypeDescriptorId
            .Should()
            .NotBe(
                replacementAddressTypeDescriptorId,
                "the original and replacement AddressType descriptor identities must differ"
            );
        addressesBefore
            .Should()
            .HaveCount(
                maxRowsPerBatch + 2,
                "the create seeds maxRowsPerBatch + 2 existing rows before the changed-descriptor update"
            );
        addressesBefore
            .Should()
            .OnlyContain(
                address => address.AddressTypeDescriptorId == originalAddressTypeDescriptorId,
                "every created row starts with the original AddressType descriptor"
            );
        addressesBefore
            .Should()
            .OnlyContain(
                address => address.SchoolDocumentId == documentId,
                "every created row is keyed to the document under test"
            );
        addressesBefore
            .Select(address => address.Ordinal)
            .Should()
            .Equal(
                Enumerable.Range(0, addressesBefore.Count),
                "the create seeds contiguous 0-based ordinals as the no-reorder baseline"
            );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        result.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(documentUuid);

        addressesAfter
            .Should()
            .HaveCount(
                addressesBefore.Count,
                "a changed-descriptor update over existing rows preserves the full rowset"
            );
        addressesAfter.Select(address => address.CollectionItemId).Should().OnlyHaveUniqueItems();

        for (int index = 0; index < addressesAfter.Count; index++)
        {
            SchoolAddressWithDescriptorRow before = addressesBefore[index];

            addressesAfter[index]
                .Should()
                .Be(
                    new SchoolAddressWithDescriptorRow(
                        before.CollectionItemId,
                        before.SchoolDocumentId,
                        before.Ordinal,
                        before.City,
                        replacementAddressTypeDescriptorId
                    ),
                    "the row at ordinal {0} keeps its stable CollectionItemId, parent, ordinal, and city while its AddressType descriptor is replaced",
                    index
                );
        }
    }

    /// <summary>
    /// Asserts the collection update-by-stable-row-identity commands were partitioned into exactly two batches
    /// at the compiled limit: parameter counts <c>[maxRowsPerBatch * parametersPerRow, 2 * parametersPerRow]</c>.
    /// </summary>
    public static void AssertUpdateBatchPartitions(
        IReadOnlyList<int> updateParameterCounts,
        int maxRowsPerBatch,
        int parametersPerRow
    ) => updateParameterCounts.Should().Equal(maxRowsPerBatch * parametersPerRow, 2 * parametersPerRow);

    /// <summary>
    /// Asserts a multi-batch delete/update reduced a large base collection to a single retained row: the
    /// create seeded maxRowsPerBatch + 2 rows, the pre-update readback matches that count, the update
    /// returned UpdateSuccess for the existing document, and exactly the retained row remains with its
    /// original CollectionItemId, document id, ordinal 0, and value. Reuses the approved omission helper
    /// for the reduced-row semantic.
    /// </summary>
    public static void AssertMultiBatchDeleteUpdateReducedToRetainedRow(
        UpdateResult result,
        DocumentUuid documentUuid,
        long documentId,
        int maxRowsPerBatch,
        int createdAddressCount,
        IReadOnlyList<NoProfileUpdateSemanticsScenarios.SchoolAddressRow> addressesBefore,
        IReadOnlyList<NoProfileUpdateSemanticsScenarios.SchoolAddressRow> addressesAfter,
        string retainedCity
    )
    {
        createdAddressCount
            .Should()
            .Be(maxRowsPerBatch + 2, "the create seeds maxRowsPerBatch + 2 rows before the delete/update");
        addressesBefore
            .Should()
            .HaveCount(createdAddressCount, "the pre-update readback matches the created row count");

        NoProfileUpdateSemanticsScenarios.AssertMultiBatchBaseCollectionReducedToRetainedRow(
            result,
            documentUuid,
            documentId,
            maxRowsPerBatch,
            addressesBefore,
            addressesAfter,
            retainedCity
        );
    }

    /// <summary>
    /// Asserts a multi-batch create of a large collection-aligned extension scope returned InsertSuccess
    /// and persisted every extension row keyed to its base address CollectionItemId in base order.
    /// </summary>
    public static void AssertLargeCollectionAlignedExtensionCreatePersisted(
        UpsertResult result,
        DocumentUuid documentUuid,
        int maxRowsPerBatch,
        int requestedAddressCount,
        DocumentRow document,
        IReadOnlyList<SchoolAddressRow> addresses,
        IReadOnlyList<SchoolExtensionAddressRow> extensionAddresses
    )
    {
        requestedAddressCount
            .Should()
            .BeGreaterThan(maxRowsPerBatch, "the request must exceed the compiled batch limit");

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        result.As<UpsertResult.InsertSuccess>().NewDocumentUuid.Should().Be(documentUuid);

        addresses.Should().HaveCount(requestedAddressCount);
        extensionAddresses.Should().HaveCount(requestedAddressCount);

        extensionAddresses
            .Should()
            .Equal(
                addresses.Select(
                    (address, index) =>
                        new SchoolExtensionAddressRow(
                            address.CollectionItemId,
                            document.DocumentId,
                            CreateZone(index)
                        )
                )
            );
    }

    /// <summary>
    /// Asserts the collection-aligned extension insert commands were partitioned into exactly two
    /// batches at the compiled limit: parameter counts <c>[maxRowsPerBatch * parametersPerRow, 2 * parametersPerRow]</c>.
    /// </summary>
    public static void AssertAlignedExtensionInsertBatchPartitions(
        IReadOnlyList<int> extensionInsertParameterCounts,
        int maxRowsPerBatch,
        int parametersPerRow
    ) =>
        extensionInsertParameterCounts
            .Should()
            .Equal(maxRowsPerBatch * parametersPerRow, 2 * parametersPerRow);

    /// <summary>Asserts the authoritative payload is large enough to exercise real parameter pressure: exactly 28 collection rows and more than 300 insert parameters.</summary>
    public static void AssertParameterPressurePayload(int collectionRowCount, int insertParameterCount)
    {
        collectionRowCount.Should().Be(28);
        insertParameterCount.Should().BeGreaterThan(300);
    }

    /// <summary>
    /// Asserts the authoritative StudentAcademicRecord large-collection create returned InsertSuccess with
    /// the expected document identity and resource key, that the root and extension rows are keyed to the
    /// created DocumentId, and that the four child collections persisted their authoritative expected row
    /// counts (12 academic honors, 12 diplomas, 2 grade point averages, 2 recognitions; 28 total) with
    /// unique CollectionItemIds. Detailed root/extension/collection value assertions stay in the provider
    /// suite, which passes actual persisted state (not request-spec counts).
    /// </summary>
    public static void AssertAuthoritativeLargeCollectionCreatePersisted(
        UpsertResult result,
        DocumentUuid documentUuid,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentRow document,
        long academicRecordRootDocumentId,
        long academicRecordExtensionDocumentId,
        IReadOnlyList<long> academicHonorCollectionItemIds,
        IReadOnlyList<long> diplomaCollectionItemIds,
        IReadOnlyList<long> gradePointAverageCollectionItemIds,
        IReadOnlyList<long> recognitionCollectionItemIds
    )
    {
        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        document.DocumentUuid.Should().Be(documentUuid.Value);
        document.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[resource]);

        academicRecordRootDocumentId
            .Should()
            .Be(document.DocumentId, "the StudentAcademicRecord root row is keyed to the created document");
        academicRecordExtensionDocumentId
            .Should()
            .Be(
                document.DocumentId,
                "the StudentAcademicRecord extension row is keyed to the created document"
            );

        AssertCollectionIdSet(academicHonorCollectionItemIds, 12, "AcademicHonors");
        AssertCollectionIdSet(diplomaCollectionItemIds, 12, "Diplomas");
        AssertCollectionIdSet(gradePointAverageCollectionItemIds, 2, "GradePointAverages");
        AssertCollectionIdSet(recognitionCollectionItemIds, 2, "Recognitions");

        (
            academicHonorCollectionItemIds.Count
            + diplomaCollectionItemIds.Count
            + gradePointAverageCollectionItemIds.Count
            + recognitionCollectionItemIds.Count
        )
            .Should()
            .Be(28, "the authoritative create persists 28 collection rows in total");
    }

    private static void AssertCollectionIdSet(
        IReadOnlyList<long> collectionItemIds,
        int expectedCount,
        string collectionName
    )
    {
        collectionItemIds
            .Should()
            .HaveCount(expectedCount, "{0} persists its authoritative expected row count", collectionName);
        collectionItemIds
            .Should()
            .OnlyHaveUniqueItems("{0} persists unique CollectionItemIds", collectionName);
    }

    /// <summary>
    /// Asserts the authoritative changed PUT returned UpdateSuccess for the existing document, kept its
    /// storage DocumentId, and bumped ContentVersion. Detailed root/extension/collection value
    /// assertions stay in the provider suite.
    /// </summary>
    public static void AssertAuthoritativeLargeCollectionChangedPutIdentity(
        UpdateResult result,
        DocumentUuid documentUuid,
        DocumentRow documentAfterCreate,
        DocumentRow documentAfterChangedPut
    )
    {
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        result.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(documentUuid);
        documentAfterChangedPut.DocumentUuid.Should().Be(documentUuid.Value);
        documentAfterChangedPut.DocumentId.Should().Be(documentAfterCreate.DocumentId);
        documentAfterChangedPut.ContentVersion.Should().BeGreaterThan(documentAfterCreate.ContentVersion);
    }

    /// <summary>
    /// Asserts a changed PUT over a large keyed child collection reused the stable CollectionItemId for
    /// every retained key, dropped the id of every omitted key, and assigned a new id to every inserted
    /// key. The maps are keyed by each collection's semantic identity (for example honor description or
    /// diploma award date). Detailed row/value/FK assertions stay in the provider suite.
    /// </summary>
    public static void AssertChangedCollectionReusesRetainedIdsAndReplacesOthers(
        string collectionName,
        IReadOnlyDictionary<string, long> createIdsByKey,
        IReadOnlyDictionary<string, long> changedIdsByKey
    )
    {
        // Guard against a vacuous pass: the ids must be unique, and the change must actually exercise a
        // retained, an omitted, and an inserted key so the per-key provenance checks below are meaningful.
        createIdsByKey
            .Values.Should()
            .OnlyHaveUniqueItems("{0} create CollectionItemIds are unique", collectionName);
        changedIdsByKey
            .Values.Should()
            .OnlyHaveUniqueItems("{0} changed CollectionItemIds are unique", collectionName);

        createIdsByKey
            .Keys.Intersect(changedIdsByKey.Keys, StringComparer.Ordinal)
            .Should()
            .NotBeEmpty("{0} must exercise at least one retained key", collectionName);
        createIdsByKey
            .Keys.Except(changedIdsByKey.Keys, StringComparer.Ordinal)
            .Should()
            .NotBeEmpty("{0} must exercise at least one omitted key", collectionName);
        changedIdsByKey
            .Keys.Except(createIdsByKey.Keys, StringComparer.Ordinal)
            .Should()
            .NotBeEmpty("{0} must exercise at least one inserted key", collectionName);

        foreach ((string key, long createId) in createIdsByKey)
        {
            if (changedIdsByKey.TryGetValue(key, out long changedId))
            {
                changedId
                    .Should()
                    .Be(
                        createId,
                        "{0} reuses the stable CollectionItemId for retained '{1}'",
                        collectionName,
                        key
                    );
            }
            else
            {
                changedIdsByKey
                    .Values.Should()
                    .NotContain(
                        createId,
                        "{0} drops the CollectionItemId of omitted '{1}'",
                        collectionName,
                        key
                    );
            }
        }

        foreach ((string key, long changedId) in changedIdsByKey)
        {
            if (!createIdsByKey.ContainsKey(key))
            {
                createIdsByKey
                    .Values.Should()
                    .NotContain(
                        changedId,
                        "{0} assigns a new CollectionItemId to inserted '{1}'",
                        collectionName,
                        key
                    );
            }
        }
    }
}
