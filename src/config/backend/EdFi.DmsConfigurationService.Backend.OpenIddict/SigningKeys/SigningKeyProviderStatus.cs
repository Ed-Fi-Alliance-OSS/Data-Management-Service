// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// A point-in-time view of the snapshot provider and its load gate (spec §4.4), for diagnostics and scheduling.
/// </summary>
public sealed record SigningKeyProviderStatus
{
    /// <exception cref="ArgumentException">
    /// <paramref name="state"/> and <paramref name="current"/> disagree: <see cref="SigningKeySnapshotState.None"/> exactly
    /// when there is no snapshot.
    /// </exception>
    public SigningKeyProviderStatus(
        SigningKeySnapshotState state,
        SigningKeySnapshot? current,
        int consecutiveFailures,
        DateTimeOffset nextAttemptAt,
        bool loadInFlight,
        SigningKeyRefreshOutcome? lastOutcome
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailures);
        if ((state == SigningKeySnapshotState.None) != (current is null))
        {
            throw new ArgumentException(
                "The state is None exactly when no snapshot has been published.",
                nameof(state)
            );
        }

        State = state;
        Current = current;
        ConsecutiveFailures = consecutiveFailures;
        NextAttemptAt = nextAttemptAt;
        LoadInFlight = loadInFlight;
        LastOutcome = lastOutcome;
    }

    public SigningKeySnapshotState State { get; }

    /// <summary>The published snapshot, or <see langword="null"/> before the first successful load.</summary>
    public SigningKeySnapshot? Current { get; }

    /// <summary>Failed attempts since the last success.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>The earliest instant the gate admits a new attempt.</summary>
    public DateTimeOffset NextAttemptAt { get; }

    public bool LoadInFlight { get; }

    /// <summary>The outcome of the last completed attempt, or <see langword="null"/> before any attempt completes.</summary>
    public SigningKeyRefreshOutcome? LastOutcome { get; }
}
