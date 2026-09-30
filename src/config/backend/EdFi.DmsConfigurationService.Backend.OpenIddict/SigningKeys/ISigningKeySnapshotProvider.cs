// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// What asked for a load; logged with every attempt (spec §4.9).
/// </summary>
public enum SigningKeyRefreshTrigger
{
    Startup,
    Timer,
    Request,
    UnknownKey,
}

/// <summary>
/// The result of an unknown-kid refresh decision (spec §4.4, §4.9).
/// </summary>
public enum SigningKeyUnknownKeyOutcome
{
    /// <summary>The key id was already in the current snapshot; no load was needed.</summary>
    AlreadyPresent,

    /// <summary>A load completed and the key id is now present.</summary>
    RefreshedFound,

    /// <summary>A load completed and the key id is still absent.</summary>
    RefreshedAbsent,

    /// <summary>No load: the last completed load finished less than the cooldown ago.</summary>
    SuppressedCooldown,

    /// <summary>No load: the retry gate is closed until its next attempt time.</summary>
    RefusedGate,

    /// <summary>A load ran and failed; the key id is not present.</summary>
    RefreshFailed,
}

/// <summary>
/// The shared signing-key state (spec D-1, §4.4): one immutable snapshot, one in-flight load at a time for every
/// trigger, and a retry gate with backoff. Request paths read keys from memory and wait asynchronously; nothing here
/// blocks a thread.
/// </summary>
public interface ISigningKeySnapshotProvider
{
    /// <summary>The published snapshot, or <see langword="null"/> before the first successful load.</summary>
    SigningKeySnapshot? Current { get; }

    SigningKeyProviderStatus Status { get; }

    /// <summary>The earliest instant the gate admits a new attempt.</summary>
    DateTimeOffset NextAttemptAt { get; }

    /// <summary>
    /// Completes at the next state transition (an attempt completing). Transitions that happen before a caller awaits
    /// are coalesced: read the property again after each wake. A signal says the state changed, never that a load is due.
    /// </summary>
    Task AttemptStateChanged { get; }

    /// <summary>
    /// Returns a usable snapshot: a fresh one directly; an overdue one directly, after requesting a load the gate may
    /// refuse; otherwise it awaits a load. <paramref name="cancellationToken"/> cancels only this caller's wait, never
    /// the shared load.
    /// </summary>
    /// <exception cref="SigningKeysUnavailableException">No usable snapshot exists and the load failed or was refused.</exception>
    Task<SigningKeySnapshot> GetUsableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts a load, or joins the one in flight, and returns its outcome; <see cref="SigningKeyRefreshOutcome.Refused"/>
    /// when the gate is closed. <paramref name="cancellationToken"/> cancels only this caller's wait.
    /// </summary>
    Task<SigningKeyRefreshOutcome> RefreshAsync(
        SigningKeyRefreshTrigger trigger,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// For a token carrying a key id the current snapshot lacks: loads at most once per cooldown (measured from the
    /// last completed load) and only when the gate is open, then reports whether the key id is present.
    /// </summary>
    Task<SigningKeyUnknownKeyOutcome> TryRefreshForUnknownKeyAsync(
        string keyId,
        CancellationToken cancellationToken
    );
}
