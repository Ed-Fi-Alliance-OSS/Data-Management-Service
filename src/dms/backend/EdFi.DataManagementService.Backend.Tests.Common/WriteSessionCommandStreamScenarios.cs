// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Provider-neutral view of one write request's session command stream. Each engine adapter
/// classifies its own dialect SQL and reports these counts, so the shared expectations below never
/// contain dialect command text.
/// </summary>
/// <param name="TotalCommandCount">Every command created on the write session.</param>
/// <param name="BeginCount">Transactions begun, counted separately from commands.</param>
/// <param name="CommitCount">Commits, counted separately from commands.</param>
/// <param name="RollbackCount">Rollbacks, counted separately from commands.</param>
/// <param name="HydrationBatchCount">
/// Current-state hydration batches. Also previously invisible to the session recorder.
/// </param>
/// <param name="DocumentUuidLookupCount">
/// Commands that resolve a document by its external <c>DocumentUuid</c>: the in-session PUT target
/// lookup. Counted separately from <paramref name="NaturalKeyCaptureCount"/> because the two
/// verbs resolve their target through different keys.
/// </param>
/// <param name="NaturalKeyLookupCount">
/// Commands carrying a natural-key reference lookup, matched on the lookup's own group parameters so that
/// other reads of a bound key list (SQL Server hydration's <c>OPENJSON</c>) never classify as one.
/// </param>
/// <param name="NaturalKeyCaptureCount">
/// Commands carrying the POST target capture by the resource's own natural key, matched on the capture's
/// <c>@nk</c> parameters. Counted apart from <paramref name="NaturalKeyLookupCount"/>: the capture binds
/// plain scalars, never a lookup group's key list.
/// </param>
public sealed record WriteSessionCommandStreamSummary(
    int TotalCommandCount,
    int BeginCount,
    int CommitCount,
    int RollbackCount,
    int HydrationBatchCount,
    int DocumentUuidLookupCount,
    int NaturalKeyLookupCount,
    int NaturalKeyCaptureCount
);

/// <summary>
/// Shared expectations over the write-session command stream.
/// </summary>
/// <remarks>
/// These are characterization assertions: they pin the command stream as it behaves today so a later
/// change to command counts is a visible, deliberate diff rather than a silent one. They deliberately
/// assert exact totals — an upper bound would not catch a regression back to a per-table N+1 pattern.
/// </remarks>
public static class WriteSessionCommandStreamScenarios
{
    /// <summary>
    /// A POST create's first phase is one composite command whose capture statement matches the
    /// resource's own natural key to observe and lock the target, which embeds the request's
    /// natural-key reference lookup, and which speculatively carries the hydration batch — the command
    /// is built before create-vs-update is known, so one command serves both branches and the create
    /// branch pays the batch's empty result sets. With one capture, one lookup, one hydration batch and
    /// only two commands, only the first command can carry all three, so neither the capture nor the
    /// lookup is a round trip of its own, and nothing observes the target again on the normal path.
    /// </summary>
    public static void AssertCreateStreamIsFullyObserved(
        WriteSessionCommandStreamSummary summary,
        int expectedTotalCommandCount
    )
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.NaturalKeyCaptureCount.Should().Be(1);
        summary.NaturalKeyLookupCount.Should().Be(1);
        summary.DocumentUuidLookupCount.Should().Be(0);
        summary.HydrationBatchCount.Should().Be(1);
        summary.TotalCommandCount.Should().Be(expectedTotalCommandCount);
        AssertCommittedTransactionBoundary(summary);
    }

    /// <summary>
    /// A PUT against an existing target resolves it by external <c>DocumentUuid</c> in the capture
    /// statement of one composite first-phase command, which also carries the hydration batch. One
    /// command classifies as both, and no natural-key capture appears on this path. The request carries no reference, so no command classifies as a
    /// natural-key lookup.
    /// </summary>
    public static void AssertUpdateStreamIsFullyObserved(
        WriteSessionCommandStreamSummary summary,
        int expectedTotalCommandCount
    )
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.HydrationBatchCount.Should().Be(1);
        summary.DocumentUuidLookupCount.Should().Be(1);
        summary.NaturalKeyCaptureCount.Should().Be(0);
        summary.NaturalKeyLookupCount.Should().Be(0);
        summary.TotalCommandCount.Should().Be(expectedTotalCommandCount);
        AssertCommittedTransactionBoundary(summary);
    }

    /// <summary>
    /// A POST that resolves to an existing document makes the same one composite first-phase command a
    /// POST create makes — the own-natural-key capture plus the hydration batch — and here the batch
    /// hydrates the locked target's current state. The capture is the only target
    /// observation: it decides create-vs-update for the attempt and is never repeated on the normal
    /// path.
    /// </summary>
    public static void AssertPostAsUpdateStreamIsFullyObserved(
        WriteSessionCommandStreamSummary summary,
        int expectedTotalCommandCount
    ) =>
        // Both POST branches make the identical first-phase command; only what its result sets carry
        // differs, which the request outcome asserts separately.
        AssertCreateStreamIsFullyObserved(summary, expectedTotalCommandCount);

    /// <summary>
    /// A PUT whose target does not exist observes that inside the write transaction and rolls back.
    /// The whole command stream is the single composite first-phase command: its capture filters on
    /// the external <c>DocumentUuid</c>, its co-batched hydration statements return empty result sets
    /// behind the absent capture, and no reference resolution or persistence runs after the missing
    /// target. Nothing commits.
    /// </summary>
    public static void AssertMissingPutTargetStreamIsFullyObserved(WriteSessionCommandStreamSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.DocumentUuidLookupCount.Should().Be(1);
        summary.NaturalKeyCaptureCount.Should().Be(0);
        summary.NaturalKeyLookupCount.Should().Be(0);
        summary.HydrationBatchCount.Should().Be(1);
        summary.TotalCommandCount.Should().Be(1);
        summary.BeginCount.Should().Be(1);
        summary.CommitCount.Should().Be(0);
        summary.RollbackCount.Should().Be(1);
    }

    /// <summary>
    /// A POST whose Create and Update policies differ takes the ordered-segments first phase: the capture
    /// runs alone as the first command, so the branch the target selects is known before any stored check,
    /// then the reference lookup runs as its own command. A create needs no hydration and goes straight to
    /// the DML command (3 commands); an update hydrates the locked target first (4 commands).
    /// </summary>
    public static void AssertOrderedSegmentsPostStreamIsFullyObserved(
        WriteSessionCommandStreamSummary summary,
        WriteSessionCommandStreamSummary firstCommand,
        int expectedTotalCommandCount,
        int expectedHydrationBatchCount
    )
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(firstCommand);

        summary.NaturalKeyCaptureCount.Should().Be(1);
        summary.NaturalKeyLookupCount.Should().Be(1);
        summary.DocumentUuidLookupCount.Should().Be(0);
        summary.HydrationBatchCount.Should().Be(expectedHydrationBatchCount);
        summary.TotalCommandCount.Should().Be(expectedTotalCommandCount);
        AssertCommittedTransactionBoundary(summary);

        // The capture is the first command and carries nothing else.
        firstCommand.NaturalKeyCaptureCount.Should().Be(1);
        firstCommand.NaturalKeyLookupCount.Should().Be(0);
        firstCommand.HydrationBatchCount.Should().Be(0);
    }

    /// <summary>
    /// BEGIN and COMMIT are recorded separately and must never be folded into a command count. A
    /// successful write commits exactly once and never rolls back.
    /// </summary>
    public static void AssertCommittedTransactionBoundary(WriteSessionCommandStreamSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.BeginCount.Should().Be(1);
        summary.CommitCount.Should().Be(1);
        summary.RollbackCount.Should().Be(0);
    }
}
