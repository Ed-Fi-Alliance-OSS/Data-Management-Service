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
/// Provider-agnostic assertion helpers for the no-profile atomic rollback family
/// (`NoProfileRollbackSafety`): a failure injected at the last write of a full-surface create rolls
/// the whole request back to its exact pre-state across every authoritative surface (document and
/// its tracking stamps, referential identity, root, nested child/grandchild, root extension,
/// aligned extension, extension-child and visit rows, and tracked-change rowsets), and a
/// key-unification conflict is rejected as a validation failure that leaves the document and
/// authoritative tables unchanged. Each provider suite keeps its own provisioning, failure
/// injection, command recording, dialect SQL/readback, and request execution; it translates its
/// recorded command text into the provider-neutral <see cref="RelationalWriteStep"/> steps and
/// neutral snapshot records defined here, then delegates the behavioral assertions. No provider
/// dialect SQL or provider driver types belong in this contract.
/// </summary>
public static class NoProfileAtomicRollbackAssertions
{
    /// <summary>The exact failure message the provider suites inject to fail a write after the early executor writes.</summary>
    public const string InjectedFailureMessage = "Injected write failure after early executor writes.";

    /// <summary>
    /// Provider-neutral semantic relational write step, declared in the production write-plan order
    /// for the full-surface create: the document row, the root row, the root extension row, then the
    /// id-reserved collection tables in plan order (extension-child interventions, base addresses,
    /// intervention visits, address periods), and finally the aligned extension rows keyed to the
    /// base collection ids. Each provider suite translates its recorded dialect command text into
    /// this ordered vocabulary so the shared assertion can reason about the write order without
    /// seeing provider SQL.
    /// </summary>
    public enum RelationalWriteStep
    {
        Document,
        School,
        SchoolExtension,
        SchoolExtensionIntervention,
        SchoolAddress,
        SchoolExtensionInterventionVisit,
        SchoolAddressPeriod,
        SchoolExtensionAddress,
    }

    /// <summary>
    /// Asserts the injected failure is an <see cref="InvalidOperationException"/> carrying the exact
    /// expected message and that it surfaced only after every full-surface write category was
    /// attempted in production write-plan order, ending at the targeted aligned-extension address
    /// write (the plan's final write). The targeted final command is recorded as attempted but never
    /// executes; every preceding category must have been genuinely attempted first, which keeps the
    /// rollback proof non-vacuous.
    /// </summary>
    public static void AssertInjectedFailureAfterOrderedEarlyWrites(
        Exception exception,
        IReadOnlyList<RelationalWriteStep> orderedWriteAttempts
    )
    {
        exception.Should().BeOfType<InvalidOperationException>();
        exception.Message.Should().Be(InjectedFailureMessage);

        List<RelationalWriteStep> steps = orderedWriteAttempts.ToList();

        int previousIndex = -1;
        foreach (RelationalWriteStep step in Enum.GetValues<RelationalWriteStep>())
        {
            int index = steps.IndexOf(step);
            index
                .Should()
                .BeGreaterThan(
                    previousIndex,
                    $"the {step} write must be attempted after every preceding full-surface write category"
                );
            previousIndex = index;
        }
    }

    /// <summary>
    /// Provider-neutral row-count snapshot of every authoritative surface the full-surface create
    /// touches: the document row (whose absence also proves its tracking stamp state is gone), the
    /// trigger-maintained referential identity, the root, the nested child and grandchild
    /// collections, the root extension, the collection-aligned extension, the extension-child and
    /// visit collections, and the root's tracked-change rowset. Each provider suite reads its own
    /// SQL into this shape before the failing request and after the rollback.
    /// </summary>
    public sealed record FullSurfaceRollbackSnapshot(
        long DocumentCount,
        long ReferentialIdentityCount,
        long SchoolCount,
        long SchoolAddressCount,
        long SchoolAddressPeriodCount,
        long SchoolExtensionCount,
        long SchoolExtensionAddressCount,
        long SchoolExtensionInterventionCount,
        long SchoolExtensionInterventionVisitCount,
        long SchoolTrackedChangeCount
    );

    /// <summary>
    /// Asserts the failed full-surface create rolled back to its exact pre-state: the pre-state
    /// snapshot is proven to be the reset/empty state (so rollback-to-pre-state is non-vacuous and
    /// cannot hide leaked rows), and the post-rollback snapshot equals it exactly across every
    /// authoritative surface, including the trigger-maintained referential identity and the
    /// tracked-change rowset.
    /// </summary>
    public static void AssertFullSurfaceRollbackToPreState(
        FullSurfaceRollbackSnapshot before,
        FullSurfaceRollbackSnapshot after
    )
    {
        before
            .Should()
            .BeEquivalentTo(
                new FullSurfaceRollbackSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                "the reset pre-state must be empty on every touched surface"
            );
        after.Should().BeEquivalentTo(before);
    }

    /// <summary>Stamp-complete document row for the rejected-write snapshot.</summary>
    public sealed record RejectedWriteDocumentRow(
        long DocumentId,
        Guid DocumentUuid,
        short ResourceKeyId,
        long ContentVersion,
        long IdentityVersion,
        DateTimeOffset ContentLastModifiedAt,
        DateTimeOffset IdentityLastModifiedAt,
        DateTimeOffset CreatedAt
    );

    public sealed record RejectedWriteReferentialIdentityRow(
        Guid ReferentialId,
        long DocumentId,
        short ResourceKeyId
    );

    /// <summary>
    /// The key-unification target: the conflicting Calendar seed's full value-bearing row, including
    /// the root table's own replicated ContentVersion/ContentLastModifiedAt stamp columns.
    /// </summary>
    public sealed record RejectedWriteCalendarRow(
        long DocumentId,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt,
        long SchoolYearDocumentId,
        int SchoolYear,
        long SchoolDocumentId,
        long SchoolId,
        long CalendarTypeDescriptorId,
        string CalendarCode
    );

    public sealed record RejectedWriteAssociationRow(
        long DocumentId,
        long SchoolIdUnified,
        int SchoolYearUnified,
        long CalendarDocumentId,
        string CalendarCode,
        long SchoolYearDocumentId,
        long SchoolDocumentId,
        long StudentDocumentId,
        string StudentUniqueId,
        long EntryGradeLevelDescriptorId,
        DateOnly EntryDate,
        bool PrimarySchool
    );

    public sealed record RejectedWriteAssociationExtensionRow(
        long DocumentId,
        long MembershipTypeDescriptorId
    );

    public sealed record RejectedWriteAlternativeGraduationPlanRow(
        long CollectionItemId,
        int Ordinal,
        long AssociationDocumentId,
        long GraduationPlanDocumentId,
        long EducationOrganizationId,
        long GraduationPlanTypeDescriptorId,
        int GraduationSchoolYear
    );

    public sealed record RejectedWriteEducationPlanRow(
        long CollectionItemId,
        int Ordinal,
        long AssociationDocumentId,
        long EducationPlanDescriptorId
    );

    /// <summary>
    /// Provider-neutral, deterministically ordered, value-bearing snapshot of every authoritative,
    /// referential-identity, and tracking surface a rejected StudentSchoolAssociation
    /// key-unification write could have touched: stamp-complete document rows, full
    /// referential-identity rows, the conflicting Calendar seed's value-bearing row, the full
    /// value-bearing rowsets of the association root/extension/collection tables (expected empty),
    /// and the association/calendar tracked-change rowset counts (their tables are expected empty,
    /// so a count captures their entire state). Each provider suite reads its own SQL into this
    /// shape before and after the rejected write.
    /// </summary>
    public sealed record RejectedWriteSnapshot(
        IReadOnlyList<RejectedWriteDocumentRow> Documents,
        IReadOnlyList<RejectedWriteReferentialIdentityRow> ReferentialIdentities,
        RejectedWriteCalendarRow ConflictCalendar,
        IReadOnlyList<RejectedWriteAssociationRow> AssociationRows,
        IReadOnlyList<RejectedWriteAssociationExtensionRow> AssociationExtensionRows,
        IReadOnlyList<RejectedWriteAlternativeGraduationPlanRow> AlternativeGraduationPlanRows,
        IReadOnlyList<RejectedWriteEducationPlanRow> EducationPlanRows,
        long AssociationTrackedChangeCount,
        long CalendarTrackedChangeCount
    );

    /// <summary>
    /// Asserts a key-unification conflict was rejected atomically: a single validation failure at
    /// <c>$.schoolReference.schoolId</c> carrying the canonical SchoolId_Unified conflict message,
    /// with the full before/after value-bearing snapshot exactly unchanged in order — document
    /// stamps, referential identities, the conflicting Calendar seed's values, association-side
    /// rowsets, and tracked-change counts — the rejected document absent, the association-side
    /// tables and tracked-change rowsets empty, the baseline document count unchanged, and the
    /// positive resource-key and conflict-seed preconditions preserved (the before snapshot must
    /// contain the seed documents and the exact conflicting Calendar row).
    /// </summary>
    public static void AssertKeyUnificationConflictRejectedAtomically(
        UpsertResult result,
        MappingSet mappingSet,
        Guid rejectedDocumentUuid,
        RejectedWriteSnapshot snapshotBefore,
        RejectedWriteSnapshot snapshotAfter,
        long conflictCalendarSeedDocumentId
    )
    {
        result.Should().BeOfType<UpsertResult.UpsertFailureValidation>();

        var validationFailure = result
            .As<UpsertResult.UpsertFailureValidation>()
            .ValidationFailures.Should()
            .ContainSingle()
            .Subject;

        validationFailure.Path.Value.Should().Be("$.schoolReference.schoolId");
        validationFailure
            .Message.Should()
            .Contain("Key-unification conflict for canonical column 'SchoolId_Unified'");

        // Non-vacuous pre-state: the seed documents exist and the conflicting Calendar seed row is
        // present with a positive resource key mapping; the tracked-change rowsets start empty.
        snapshotBefore.Documents.Should().NotBeEmpty();
        snapshotBefore.ConflictCalendar.DocumentId.Should().Be(conflictCalendarSeedDocumentId);
        snapshotBefore.AssociationTrackedChangeCount.Should().Be(0);
        snapshotBefore.CalendarTrackedChangeCount.Should().Be(0);
        mappingSet
            .ResourceKeyIdByResource[new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation")]
            .Should()
            .BeGreaterThan((short)0);
        conflictCalendarSeedDocumentId.Should().BeGreaterThan(0L);

        // The rejected write left every value-bearing surface exactly as it was, in order.
        snapshotAfter.Should().BeEquivalentTo(snapshotBefore, options => options.WithStrictOrdering());
        snapshotAfter
            .Documents.Select(document => document.DocumentUuid)
            .Should()
            .NotContain(rejectedDocumentUuid);
        snapshotAfter.AssociationRows.Should().BeEmpty();
        snapshotAfter.AssociationExtensionRows.Should().BeEmpty();
        snapshotAfter.AlternativeGraduationPlanRows.Should().BeEmpty();
        snapshotAfter.EducationPlanRows.Should().BeEmpty();
        snapshotAfter.Documents.Count.Should().Be(snapshotBefore.Documents.Count);
    }
}
