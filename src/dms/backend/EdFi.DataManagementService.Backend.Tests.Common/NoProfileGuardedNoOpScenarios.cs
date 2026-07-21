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
/// Provider-agnostic scenario assertions for the no-profile guarded no-op family
/// (`NoProfileGuardedNoOp`): an unchanged PUT / POST-as-update compares the post-merge rowset to the
/// current state and skips DML, revalidating freshness before returning no-op. Each provider suite
/// keeps its own provisioning, dialect SQL/readback, and freshness/current-state-loader/commit-window
/// instrumentation and orchestration; it projects actual before/after readback into the full neutral
/// <see cref="PersistedState"/> and passes primitive probe observations, then delegates the behavioral
/// assertions. No provider driver types, dialect SQL, or instrumentation types belong in this contract.
/// </summary>
public static class NoProfileGuardedNoOpScenarios
{
    /// <summary>
    /// The deterministic ContentLastModifiedAt stamp every injected concurrent content-version bump
    /// writes alongside its version increment, mirroring production stamping. It is deliberately far
    /// from any live clock so retention of the exact concurrent stamp (rather than a fresh DMS
    /// rewrite) is provable and non-vacuous.
    /// </summary>
    public static readonly DateTimeOffset ConcurrentBumpContentLastModifiedAt = new(
        2030,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero
    );

    public sealed record DocumentRow(
        long DocumentId,
        Guid DocumentUuid,
        short ResourceKeyId,
        long ContentVersion,
        long IdentityVersion,
        DateTimeOffset ContentLastModifiedAt,
        DateTimeOffset IdentityLastModifiedAt,
        DateTimeOffset CreatedAt
    );

    public sealed record SchoolRow(
        long DocumentId,
        long SchoolId,
        string? ShortName,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt
    );

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
        IReadOnlyList<SchoolExtensionAddressRow> ExtensionAddresses,
        long DocumentCount
    );

    /// <summary>Asserts an unchanged PUT returned UpdateSuccess for the existing document.</summary>
    public static void AssertPutNoOpOutcome(UpdateResult result, DocumentUuid existingDocumentUuid)
    {
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        result.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(existingDocumentUuid);
    }

    /// <summary>Asserts an unchanged POST-as-update returned UpdateSuccess for the existing document and created no row for the incoming request UUID.</summary>
    public static void AssertPostAsUpdateNoOpOutcome(
        UpsertResult result,
        DocumentUuid existingDocumentUuid,
        long incomingDocumentUuidCount
    )
    {
        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        result.As<UpsertResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(existingDocumentUuid);
        incomingDocumentUuidCount.Should().Be(0);
    }

    /// <summary>
    /// Asserts a guarded no-op left the full persisted rowset, ContentVersion, and every stored
    /// update-tracking stamp (ContentLastModifiedAt, IdentityVersion, IdentityLastModifiedAt,
    /// CreatedAt) unchanged, with the expected resource key. Guards the focused snapshot against
    /// vacuity (one document, two base and two aligned rows) before the full equality check.
    /// </summary>
    public static void AssertRowsetUnchanged(
        PersistedState before,
        PersistedState after,
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        GuardFocusedSnapshotIsNonVacuous(before);

        after.Should().BeEquivalentTo(before);
        after.Document.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[resource]);
    }

    /// <summary>
    /// Asserts a guarded no-op after a full-surface reorder left the full rowset and ContentVersion
    /// unchanged, first proving the pre-no-op base collection is actually in reordered order
    /// (Dallas, Austin) so the no-op is compared against the reordered state.
    /// </summary>
    public static void AssertRowsetUnchangedAfterReorder(
        PersistedState before,
        PersistedState after,
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        // The pre-no-op state must reflect the full-surface reorder (Dallas, Austin) before comparison.
        before.Addresses.Select(address => address.City).Should().Equal("Dallas", "Austin");

        AssertRowsetUnchanged(before, after, mappingSet, resource);
    }

    /// <summary>
    /// Asserts a guarded no-op left the full rowset unchanged except for exactly one concurrent
    /// content-stamp write: ContentVersion is before + 1 and ContentLastModifiedAt is exactly the
    /// injected concurrent sentinel (proving the concurrent outcome was retained without an extra DMS
    /// stamp/write), while IdentityVersion, IdentityLastModifiedAt, CreatedAt, and every rowset stay
    /// unchanged, with the expected resource key. Guards the focused snapshot against vacuity and the
    /// sentinel against matching the pre-state (which would make retention unprovable) before the
    /// equality check.
    /// </summary>
    public static void AssertRowsetUnchangedExceptOneContentVersionBump(
        PersistedState before,
        PersistedState after,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DateTimeOffset concurrentContentLastModifiedAt
    )
    {
        GuardFocusedSnapshotIsNonVacuous(before);
        concurrentContentLastModifiedAt
            .Should()
            .NotBe(
                before.Document.ContentLastModifiedAt,
                "the concurrent sentinel must differ from the pre-state stamp for retention to be provable"
            );

        PersistedState afterWithBeforeContentStamps = after with
        {
            Document = after.Document with
            {
                ContentVersion = before.Document.ContentVersion,
                ContentLastModifiedAt = before.Document.ContentLastModifiedAt,
            },
        };

        afterWithBeforeContentStamps.Should().BeEquivalentTo(before);
        after.Document.ContentVersion.Should().Be(before.Document.ContentVersion + 1);
        after.Document.ContentLastModifiedAt.Should().Be(concurrentContentLastModifiedAt);
        after.Document.ResourceKeyId.Should().Be(mappingSet.ResourceKeyIdByResource[resource]);
    }

    /// <summary>
    /// Asserts the current-state-refresh probe observed exactly one injected content-version bump, one
    /// current-state load, and a single loaded ContentVersion equal to the pre-update version plus one
    /// (so the guarded no-op saw the concurrently refreshed state without a repository retry).
    /// </summary>
    public static void AssertCurrentStateRefreshObservations(
        int contentVersionBumpCallCount,
        int loadCallCount,
        IReadOnlyList<long> loadedContentVersions,
        long beforeContentVersion
    )
    {
        contentVersionBumpCallCount.Should().Be(1);
        loadCallCount.Should().Be(1);
        loadedContentVersions.Should().Equal(beforeContentVersion + 1);
    }

    /// <summary>Asserts the commit-window freshness probe observed exactly two calls with results [false, true] (stale, then fresh on retry).</summary>
    public static void AssertCommitWindowFreshnessObservations(
        int isCurrentCallCount,
        IReadOnlyList<bool> freshnessResults
    )
    {
        isCurrentCallCount.Should().Be(2);
        freshnessResults.Should().Equal(false, true);
    }

    private static void GuardFocusedSnapshotIsNonVacuous(PersistedState state)
    {
        state.DocumentCount.Should().Be(1, "the focused snapshot contains exactly one document");
        state.Addresses.Should().HaveCount(2, "the focused snapshot contains the two base address rows");
        state
            .ExtensionAddresses.Should()
            .HaveCount(2, "the focused snapshot contains the two aligned extension address rows");
    }
}
