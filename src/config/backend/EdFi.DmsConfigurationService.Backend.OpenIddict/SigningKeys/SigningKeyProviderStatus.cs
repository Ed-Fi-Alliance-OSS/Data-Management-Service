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
        SigningKeyRefreshOutcome? lastOutcome,
        bool storeOperationOutstanding = false,
        long stateVersion = 0,
        bool providerDisposed = false
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailures);
        ArgumentOutOfRangeException.ThrowIfNegative(stateVersion);
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
        StoreOperationOutstanding = storeOperationOutstanding;
        StateVersion = stateVersion;
        ProviderDisposed = providerDisposed;
    }

    public SigningKeySnapshotState State { get; }

    /// <summary>The published snapshot, or <see langword="null"/> before the first successful load.</summary>
    public SigningKeySnapshot? Current { get; }

    /// <summary>Failed attempts since the last success.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>The earliest instant the gate admits a new attempt.</summary>
    public DateTimeOffset NextAttemptAt { get; }

    public bool LoadInFlight { get; }

    /// <summary>
    /// Whether a store operation is running, including one that outlived its attempt's deadline. While one is, the gate
    /// starts no other.
    /// </summary>
    public bool StoreOperationOutstanding { get; }

    /// <summary>The outcome of the last completed attempt, or <see langword="null"/> before any attempt completes.</summary>
    public SigningKeyRefreshOutcome? LastOutcome { get; }

    /// <summary>
    /// Identifies the provider state this status describes; it changes at every transition (an attempt starting or
    /// completing, an outstanding store operation ending, disposal). A conditional refresh passes it back so the
    /// provider can refuse when the state is no longer the one observed.
    /// </summary>
    public long StateVersion { get; }

    /// <summary>Whether the provider has been disposed; it then admits no attempt.</summary>
    public bool ProviderDisposed { get; }
}
