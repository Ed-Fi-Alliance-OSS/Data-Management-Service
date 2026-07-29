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
/// <param name="ReferentialIdentityLookupCount">
/// Commands that read <c>dms.ReferentialIdentity</c>: the in-session POST target lookup and bulk
/// reference resolution. Both were invisible to the session recorder before they were routed
/// through the session's command-creation seam.
/// </param>
/// <param name="HydrationBatchCount">
/// Current-state hydration batches. Also previously invisible to the session recorder.
/// </param>
public sealed record WriteSessionCommandStreamSummary(
    int TotalCommandCount,
    int BeginCount,
    int CommitCount,
    int RollbackCount,
    int ReferentialIdentityLookupCount,
    int HydrationBatchCount
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
    /// A POST create must show the in-session target lookup, which reads
    /// <c>dms.ReferentialIdentity</c> and was invisible to the session recorder before it was routed
    /// through the session's command-creation seam. A create hydrates no current state.
    /// </summary>
    public static void AssertCreateStreamIsFullyObserved(
        WriteSessionCommandStreamSummary summary,
        int expectedTotalCommandCount
    )
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.ReferentialIdentityLookupCount.Should().Be(1);
        summary.HydrationBatchCount.Should().Be(0);
        summary.TotalCommandCount.Should().Be(expectedTotalCommandCount);
        AssertCommittedTransactionBoundary(summary);
    }

    /// <summary>
    /// A PUT against an existing target must show the hydration batch, which the session recorder
    /// could not see before hydration was routed through the session. PUT resolves its target before
    /// the session opens, so no in-session <c>dms.ReferentialIdentity</c> read appears here yet.
    /// </summary>
    public static void AssertUpdateStreamIsFullyObserved(
        WriteSessionCommandStreamSummary summary,
        int expectedTotalCommandCount
    )
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.HydrationBatchCount.Should().Be(1);
        summary.ReferentialIdentityLookupCount.Should().Be(0);
        summary.TotalCommandCount.Should().Be(expectedTotalCommandCount);
        AssertCommittedTransactionBoundary(summary);
    }

    /// <summary>
    /// A POST that resolves to an existing document hydrates current state but issues no in-session
    /// target lookup.
    /// </summary>
    /// <remarks>
    /// The executor only re-resolves a POST target in-session when the incoming target context is
    /// <c>CreateNew</c> or the request carries an etag precondition. Here the pre-session lookup
    /// already resolved an existing document, so the in-session re-lookup is skipped and the only
    /// target resolution for this request happened outside the write transaction — on a separate
    /// connection the session recorder cannot see. That is the duplicate-and-outside-the-transaction
    /// resolution DMS-1332 later moves into the session; pinning it at zero here makes that move a
    /// visible diff.
    /// </remarks>
    public static void AssertPostAsUpdateStreamIsFullyObserved(
        WriteSessionCommandStreamSummary summary,
        int expectedTotalCommandCount
    )
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.ReferentialIdentityLookupCount.Should().Be(0);
        summary.HydrationBatchCount.Should().Be(1);
        summary.TotalCommandCount.Should().Be(expectedTotalCommandCount);
        AssertCommittedTransactionBoundary(summary);
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
