// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The time bounds of claims, ownership-dependent writes, and fences (spec D-3 to D-6, A3, §6.1). The two
/// configurable bounds are derived from <c>JobOptions</c> at registration: <see cref="RenewalTimeout"/> is
/// <c>RenewalInterval / 2</c>, the one deadline for a whole renewal or outcome write, and
/// <see cref="FenceTimeout"/> caps a fence's work, revalidation, and commit. The others are fixed.
/// </summary>
public sealed record JobLeaseTimings(TimeSpan RenewalTimeout, TimeSpan FenceTimeout)
{
    /// <summary>The server-side lock wait of an ownership-dependent write (D-4).</summary>
    public static TimeSpan WriteLockWait { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The server-side lock wait of a fence's row lock (D-5).</summary>
    public static TimeSpan FenceLockWait { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The fence acquisition's own command timeout, independent of the fence deadline and above
    /// <see cref="FenceLockWait"/>, so a lock wait always ends in the provider's lock-timeout error (A3).
    /// </summary>
    public static TimeSpan FenceAcquisitionTimeout { get; } = FenceLockWait + TimeSpan.FromSeconds(1);

    /// <summary>A fence needs at least this much lease left, by database time, once its lock is held.</summary>
    public static TimeSpan FenceMinimumRemainingLease { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Held back from the remaining lease when the fence deadline is computed, absorbing the latency of
    /// returning the acquisition result (D-5 step 4).
    /// </summary>
    public static TimeSpan FenceLeaseReserve { get; } = TimeSpan.FromSeconds(1);

    /// <summary>The command timeout of a claim and of each <c>Exhaust</c> batch.</summary>
    public static TimeSpan ClaimCommandTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The most rows one <c>Exhaust</c> batch changes (A4).</summary>
    public const int ExhaustBatchSize = 1_000;
}
