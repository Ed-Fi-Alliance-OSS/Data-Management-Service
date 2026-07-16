// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Provider-agnostic scenario assertions for the no-profile POST-as-update family
/// (`NoProfilePostAsUpdate`): a POST that resolves to an existing document updates it in place, an
/// immutable-identity change is rejected without committing, a stale create candidate converts to a
/// POST-as-update after a competing create commits, and authoritative DS-5.2 / StudentAcademicRecord
/// POST-as-updates update in place (with a repeat POST-as-update being a no-op). Each provider suite
/// keeps its own provisioning, race orchestration, dialect SQL/readback, and request execution; it
/// projects actual before/after readback into the neutral records and snapshots defined here, then
/// delegates the behavioral assertions. No provider driver types, dialect SQL, or race-hook types
/// belong in this contract.
/// </summary>
public static class NoProfilePostAsUpdateScenarios
{
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

    public sealed record SchoolYearTypeRow(
        long DocumentId,
        int SchoolYear,
        bool CurrentSchoolYear,
        string SchoolYearDescription
    );

    public sealed record CollectionRowSnapshot(long CollectionItemId, int Ordinal, string Key);

    public sealed record AuthoritativePostAsUpdateSnapshot(
        long DocumentId,
        long ContentVersion,
        long AcademicRecordDocumentId,
        long AcademicRecordExtensionDocumentId,
        IReadOnlyList<CollectionRowSnapshot> AcademicHonors,
        IReadOnlyList<CollectionRowSnapshot> Diplomas,
        IReadOnlyList<CollectionRowSnapshot> GradePointAverages,
        IReadOnlyList<CollectionRowSnapshot> Recognitions
    );

    /// <summary>
    /// Asserts a POST-as-update updated the existing document in place: UpdateSuccess for the existing
    /// UUID, the same storage DocumentId before and after, the expected resource key, a bumped
    /// ContentVersion, exactly one document, and no row created for the incoming request UUID.
    /// </summary>
    public static void AssertUpdatedExistingDocumentInPlace(
        UpsertResult result,
        DocumentUuid existingDocumentUuid,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentRow documentBefore,
        DocumentRow documentAfter,
        long documentCount,
        long incomingDocumentUuidCount
    )
    {
        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        result.As<UpsertResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(existingDocumentUuid);
        documentAfter.DocumentUuid.Should().Be(existingDocumentUuid.Value);
        documentAfter
            .DocumentId.Should()
            .Be(documentBefore.DocumentId, "the POST-as-update updates the existing document row in place");
        documentAfter.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[resource]);
        documentAfter.ContentVersion.Should().BeGreaterThan(documentBefore.ContentVersion);
        documentCount.Should().Be(1);
        incomingDocumentUuidCount.Should().Be(0, "the incoming request UUID does not create a new document");
    }

    /// <summary>
    /// Asserts the focused full-surface POST-as-update applied changed root state (cleared shortName),
    /// kept both base address rows by their stable CollectionItemIds/ordinals/values, and removed the
    /// omitted collection-aligned extension row while keeping the retained one keyed to its base id.
    /// </summary>
    public static void AssertFocusedFullSurfaceStateApplied(
        DocumentRow documentAfter,
        SchoolRow schoolAfter,
        IReadOnlyList<SchoolAddressRow> addressesBefore,
        IReadOnlyList<SchoolExtensionAddressRow> extensionAddressesBefore,
        IReadOnlyList<SchoolAddressRow> addressesAfter,
        IReadOnlyList<SchoolExtensionAddressRow> extensionAddressesAfter
    )
    {
        addressesBefore.Should().HaveCount(2);
        extensionAddressesBefore.Should().HaveCount(2);

        schoolAfter.Should().Be(new SchoolRow(documentAfter.DocumentId, 255901, null));

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

    /// <summary>Asserts a POST-as-update that changes an immutable identity is rejected with the explicit UpsertFailureImmutableIdentity result and exact message (never UnknownFailure).</summary>
    public static void AssertImmutableIdentityRejected(UpsertResult result, string expectedFailureMessage)
    {
        result.Should().BeOfType<UpsertResult.UpsertFailureImmutableIdentity>();
        result.Should().NotBeOfType<UpsertResult.UnknownFailure>();
        result
            .As<UpsertResult.UpsertFailureImmutableIdentity>()
            .FailureMessage.Should()
            .Be(expectedFailureMessage);
    }

    /// <summary>Asserts a rejected POST-as-update committed no row changes: the document and School rows are byte-for-byte unchanged, exactly one document remains, and no row was created for the rejected request UUID.</summary>
    public static void AssertRejectedPostAsUpdateCommittedNoChanges(
        DocumentRow documentBefore,
        DocumentRow documentAfter,
        SchoolRow schoolBefore,
        SchoolRow schoolAfter,
        long documentCount,
        long incomingDocumentUuidCount
    )
    {
        documentAfter.Should().Be(documentBefore);
        schoolAfter.Should().Be(schoolBefore);
        documentCount.Should().Be(1);
        incomingDocumentUuidCount.Should().Be(0);
    }

    /// <summary>
    /// Asserts a stale create candidate converted into a POST-as-update after the competing create
    /// committed: the winner is an InsertSuccess for its UUID with a valid composed ETag, the stale
    /// candidate becomes an UpdateSuccess for the winner UUID, only the winner document remains, and no
    /// row was created for the stale candidate UUID.
    /// </summary>
    public static void AssertStaleCreateConvertedToPostAsUpdate(
        UpsertResult createWinnerResult,
        DocumentUuid createWinnerDocumentUuid,
        UpsertResult staleCreateCandidateResult,
        DocumentRow documentAfter,
        long documentCount,
        long staleCreateCandidateUuidCount
    )
    {
        var createWinnerSuccess = createWinnerResult.Should().BeOfType<UpsertResult.InsertSuccess>().Subject;
        createWinnerSuccess.NewDocumentUuid.Should().Be(createWinnerDocumentUuid);
        RelationalGetIntegrationTestHelper.AssertComposedEtag(createWinnerSuccess.ETag);

        staleCreateCandidateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        staleCreateCandidateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(createWinnerDocumentUuid);

        documentAfter.DocumentUuid.Should().Be(createWinnerDocumentUuid.Value);
        documentCount.Should().Be(1);
        staleCreateCandidateUuidCount.Should().Be(0);
    }

    /// <summary>Asserts the last-writer state was applied to the existing document instead of creating duplicate rows: the root row carries the last-writer shortName and the single base address row is keyed to the document with ordinal 0 and the last-writer value.</summary>
    public static void AssertLastWriterStateApplied(
        DocumentRow documentAfter,
        SchoolRow schoolAfter,
        IReadOnlyList<SchoolAddressRow> addressesAfter,
        string expectedShortName,
        string expectedCity
    )
    {
        schoolAfter.Should().Be(new SchoolRow(documentAfter.DocumentId, 255901, expectedShortName));
        addressesAfter.Should().ContainSingle();
        addressesAfter[0].SchoolDocumentId.Should().Be(documentAfter.DocumentId);
        addressesAfter[0].Ordinal.Should().Be(0);
        addressesAfter[0].City.Should().Be(expectedCity);
    }

    /// <summary>Asserts the authoritative DS-5.2 SchoolYearType row was updated in place, keyed to the existing DocumentId with the expected changed SchoolYear/CurrentSchoolYear/description values.</summary>
    public static void AssertAuthoritativeSchoolYearTypeRowInPlace(
        DocumentRow documentAfter,
        SchoolYearTypeRow schoolYearTypeAfter,
        int expectedSchoolYear,
        bool expectedCurrentSchoolYear,
        string expectedSchoolYearDescription
    ) =>
        schoolYearTypeAfter
            .Should()
            .Be(
                new SchoolYearTypeRow(
                    documentAfter.DocumentId,
                    expectedSchoolYear,
                    expectedCurrentSchoolYear,
                    expectedSchoolYearDescription
                )
            );

    /// <summary>Asserts the authoritative StudentAcademicRecord root and extension rows were updated in place, both keyed to the existing DocumentId.</summary>
    public static void AssertAuthoritativeRootAndExtensionInPlace(
        long academicRecordRootDocumentId,
        long academicRecordExtensionDocumentId,
        long expectedDocumentId
    )
    {
        academicRecordRootDocumentId
            .Should()
            .Be(expectedDocumentId, "the StudentAcademicRecord root row is updated in place");
        academicRecordExtensionDocumentId
            .Should()
            .Be(expectedDocumentId, "the StudentAcademicRecord extension row is updated in place");
    }

    /// <summary>
    /// Asserts a retained child collection reused the stable CollectionItemId for its retained first row,
    /// removed the omitted second row's id, and assigned the replacement a new id. Reuses the approved
    /// omission helper so the family's canonical class visibly owns this behavior.
    /// </summary>
    public static void AssertRetainedChildCollectionIdReuse(
        string collectionName,
        IReadOnlyList<long> createCollectionItemIds,
        IReadOnlyList<long> postAsUpdateCollectionItemIds
    ) =>
        NoProfileUpdateSemanticsScenarios.AssertRetainedChildRowIdStableAndOmittedRowReplaced(
            collectionName,
            createCollectionItemIds,
            postAsUpdateCollectionItemIds
        );

    /// <summary>
    /// Asserts a repeat authoritative POST-as-update was a no-op: UpdateSuccess for the existing UUID, no
    /// row created for the incoming request UUID, and the full persisted rowset and ContentVersion are
    /// unchanged from the first POST-as-update. The snapshot must be non-vacuous (its child collections
    /// carry rows), so an empty-to-empty comparison cannot pass silently.
    /// </summary>
    public static void AssertRepeatPostAsUpdateNoOp(
        UpsertResult result,
        DocumentUuid existingDocumentUuid,
        AuthoritativePostAsUpdateSnapshot snapshotAfterFirstPostAsUpdate,
        AuthoritativePostAsUpdateSnapshot snapshotAfterRepeatPostAsUpdate,
        long incomingDocumentUuidCount
    )
    {
        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        result.As<UpsertResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(existingDocumentUuid);
        incomingDocumentUuidCount.Should().Be(0);

        snapshotAfterFirstPostAsUpdate
            .AcademicHonors.Should()
            .NotBeEmpty("the no-op snapshot must be non-vacuous");
        snapshotAfterFirstPostAsUpdate.Diplomas.Should().NotBeEmpty("the no-op snapshot must be non-vacuous");
        snapshotAfterFirstPostAsUpdate
            .GradePointAverages.Should()
            .NotBeEmpty("the no-op snapshot must be non-vacuous");
        snapshotAfterFirstPostAsUpdate
            .Recognitions.Should()
            .NotBeEmpty("the no-op snapshot must be non-vacuous");

        snapshotAfterRepeatPostAsUpdate.Should().BeEquivalentTo(snapshotAfterFirstPostAsUpdate);
    }
}
