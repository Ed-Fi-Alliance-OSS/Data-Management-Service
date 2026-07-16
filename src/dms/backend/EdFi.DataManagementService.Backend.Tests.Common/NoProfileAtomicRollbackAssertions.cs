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
/// (`NoProfileRollbackSafety`): a failure after early relational writes rolls the whole request back
/// leaving no partial state, and a key-unification conflict is rejected as a validation failure that
/// leaves the document and authoritative tables unchanged. Each provider suite keeps its own
/// provisioning, failure injection, command recording, dialect SQL/readback, and request execution;
/// it translates its recorded command text into the provider-neutral <see cref="RelationalWriteStep"/>
/// steps and neutral snapshot records defined here, then delegates the behavioral assertions. No
/// provider dialect SQL or provider driver types belong in this contract.
/// </summary>
public static class NoProfileAtomicRollbackAssertions
{
    /// <summary>The exact failure message the provider suites inject to fail a write after the early executor writes.</summary>
    public const string InjectedFailureMessage = "Injected write failure after early executor writes.";

    /// <summary>
    /// Provider-neutral semantic relational write step. Each provider suite translates its recorded
    /// dialect command text into this ordered vocabulary so the shared assertion can reason about the
    /// write order without seeing provider SQL.
    /// </summary>
    public enum RelationalWriteStep
    {
        Document,
        School,
        SchoolAddress,
    }

    /// <summary>
    /// Asserts the injected failure is an <see cref="InvalidOperationException"/> carrying the exact
    /// expected message and that it surfaced only after the Document, then School, then SchoolAddress
    /// write attempts were made in that order.
    /// </summary>
    public static void AssertInjectedFailureAfterOrderedEarlyWrites(
        Exception exception,
        IReadOnlyList<RelationalWriteStep> orderedWriteAttempts
    )
    {
        exception.Should().BeOfType<InvalidOperationException>();
        exception.Message.Should().Be(InjectedFailureMessage);

        List<RelationalWriteStep> steps = orderedWriteAttempts.ToList();
        int documentIndex = steps.IndexOf(RelationalWriteStep.Document);
        int schoolIndex = steps.IndexOf(RelationalWriteStep.School);
        int schoolAddressIndex = steps.IndexOf(RelationalWriteStep.SchoolAddress);

        documentIndex.Should().BeGreaterThanOrEqualTo(0, "the Document row is written first");
        schoolIndex.Should().BeGreaterThan(documentIndex, "the School row is written after the Document row");
        schoolAddressIndex
            .Should()
            .BeGreaterThan(schoolIndex, "the SchoolAddress write is attempted after the School row");
    }

    /// <summary>Asserts the failed create rolled back completely, leaving no Document, School, or SchoolAddress rows.</summary>
    public static void AssertNoPartialRelationalStateAfterRollback(
        long documentCount,
        long schoolCount,
        long schoolAddressCount
    )
    {
        documentCount.Should().Be(0);
        schoolCount.Should().Be(0);
        schoolAddressCount.Should().Be(0);
    }

    /// <summary>
    /// Provider-neutral snapshot of the tables a rejected StudentSchoolAssociation key-unification write
    /// would have touched. Each provider suite reads its own SQL into this shape before and after the
    /// rejected write.
    /// </summary>
    public sealed record RejectedWriteSnapshot(
        IReadOnlyList<Guid> DocumentUuids,
        IReadOnlyList<long> AssociationDocumentIds,
        IReadOnlyList<long> AssociationExtensionDocumentIds,
        IReadOnlyList<long> AlternativeGraduationPlanCollectionItemIds,
        IReadOnlyList<long> EducationPlanCollectionItemIds
    );

    /// <summary>
    /// Asserts a key-unification conflict was rejected atomically: a single validation failure at
    /// <c>$.schoolReference.schoolId</c> carrying the canonical SchoolId_Unified conflict message, with
    /// the full before/after snapshot unchanged, the rejected document absent, the association,
    /// extension, and collection target lists empty, the baseline document count unchanged, and the
    /// positive resource-key and seed-document preconditions preserved.
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

        // The rejected write left every target table exactly as it was.
        snapshotAfter.Should().BeEquivalentTo(snapshotBefore);
        snapshotAfter.DocumentUuids.Should().NotContain(rejectedDocumentUuid);
        snapshotAfter.AssociationDocumentIds.Should().BeEmpty();
        snapshotAfter.AssociationExtensionDocumentIds.Should().BeEmpty();
        snapshotAfter.AlternativeGraduationPlanCollectionItemIds.Should().BeEmpty();
        snapshotAfter.EducationPlanCollectionItemIds.Should().BeEmpty();
        snapshotAfter.DocumentUuids.Count.Should().Be(snapshotBefore.DocumentUuids.Count);

        // Positive preconditions: the resource is mapped and the conflicting seed document exists.
        mappingSet
            .ResourceKeyIdByResource[new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation")]
            .Should()
            .BeGreaterThan((short)0);
        conflictCalendarSeedDocumentId.Should().BeGreaterThan(0L);
    }
}
