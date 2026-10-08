// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Why a load attempt failed (spec D-4).
/// </summary>
public enum SigningKeyFailureKind
{
    /// <summary>The keys could not be read: the store failed, timed out, or the attempt was canceled.</summary>
    Retrieval,

    /// <summary>Key records were read, but none of them could be turned into a usable key.</summary>
    Processing,
}

/// <summary>
/// The result of one signing-key load attempt (spec D-4). Only <see cref="Succeeded"/> carries a snapshot, and a
/// failed load never replaces the current one. In particular a failed retrieval is <see cref="Failed"/>, never an
/// empty <see cref="Succeeded"/> (I-8).
/// </summary>
public abstract record SigningKeyRefreshOutcome
{
    private SigningKeyRefreshOutcome() { }

    /// <summary>
    /// The keys were retrieved and published. <c>Succeeded(0)</c> means the store holds no active key; a partial
    /// result, where some records could not be used, is <c>Succeeded(n)</c> with <see cref="DiscardedEntryCount"/> set.
    /// </summary>
    public sealed record Succeeded : SigningKeyRefreshOutcome
    {
        /// <exception cref="ArgumentException">
        /// The snapshot is empty although records were discarded. Records that exist but none of which can be used are
        /// a <see cref="SigningKeyRefreshOutcome.Failed"/> outcome of kind
        /// <see cref="SigningKeyFailureKind.Processing"/>, not an empty key set.
        /// </exception>
        public Succeeded(SigningKeySnapshot snapshot, int discardedEntryCount = 0)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentOutOfRangeException.ThrowIfNegative(discardedEntryCount);

            if (snapshot.Keys.Count == 0 && discardedEntryCount > 0)
            {
                throw new ArgumentException(
                    "Key records that all failed to parse are a processing failure, not an empty key set.",
                    nameof(discardedEntryCount)
                );
            }

            Snapshot = snapshot;
            DiscardedEntryCount = discardedEntryCount;
        }

        public SigningKeySnapshot Snapshot { get; }

        /// <summary>Key records that were read but could not be used.</summary>
        public int DiscardedEntryCount { get; }

        public int KeyCount => Snapshot.Keys.Count;
    }

    /// <summary>The attempt ran and failed; the current snapshot, if any, is unchanged.</summary>
    public sealed record Failed : SigningKeyRefreshOutcome
    {
        public Failed(SigningKeyFailureKind kind, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            Kind = kind;
            Exception = exception;
        }

        public SigningKeyFailureKind Kind { get; }

        /// <summary>The failure. Log its type and a sanitized message, never key material.</summary>
        public Exception Exception { get; }
    }

    /// <summary>
    /// No attempt was started: the retry gate is closed until <see cref="NextAttemptAt"/>, or an earlier store
    /// operation that outlived its deadline is still running (<see cref="Reason"/>).
    /// </summary>
    public sealed record Refused(
        DateTimeOffset NextAttemptAt,
        SigningKeyRefusalReason Reason = SigningKeyRefusalReason.RetryDelay
    ) : SigningKeyRefreshOutcome;
}

/// <summary>
/// Why the load gate refused an attempt.
/// </summary>
public enum SigningKeyRefusalReason
{
    /// <summary>A failure closed the gate until its retry deadline.</summary>
    RetryDelay,

    /// <summary>
    /// A store operation that outlived its load deadline has not finished. The single-flight slot owns it until it does,
    /// so no overlapping store call starts.
    /// </summary>
    OperationOutstanding,

    /// <summary>
    /// A conditional (scheduled) refresh found the provider's state changed since the caller observed it; nothing was
    /// started, and the caller should recompute from the current status.
    /// </summary>
    StateChanged,
}
