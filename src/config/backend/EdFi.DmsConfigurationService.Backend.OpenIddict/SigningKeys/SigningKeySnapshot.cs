// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Where a snapshot's keys came from.
/// </summary>
public enum SigningKeySource
{
    /// <summary>The <c>OpenIddictKey</c> table.</summary>
    Database,

    /// <summary>An X.509 certificate file.</summary>
    Certificate,
}

/// <summary>
/// How usable the current snapshot is at a given instant (spec §4.4).
/// </summary>
public enum SigningKeySnapshotState
{
    /// <summary>No snapshot has been published yet.</summary>
    None,

    /// <summary>
    /// No older than <see cref="SigningKeySettings.RefreshInterval"/> (see <see cref="SigningKeySnapshot.GetAge"/>).
    /// </summary>
    Fresh,

    /// <summary>
    /// Older than the refresh interval but no older than <see cref="SigningKeySettings.MaxStaleness"/>: still served,
    /// and a reload is due.
    /// </summary>
    Overdue,

    /// <summary>Older than <see cref="SigningKeySettings.MaxStaleness"/>: no longer trusted, so requests fail closed.</summary>
    Expired,
}

/// <summary>
/// An immutable set of public signing keys from one successful retrieval (spec D-1, D-4). A snapshot may hold zero
/// keys when the store genuinely has none (<c>Succeeded(0)</c>); a failed retrieval never produces a snapshot.
/// </summary>
public sealed class SigningKeySnapshot
{
    private readonly SigningKeyEntry[] _entries;

    public SigningKeySnapshot(
        IEnumerable<SigningKeyEntry> keys,
        DateTimeOffset retrievedAt,
        long retrievedAtTimestamp,
        long version,
        SigningKeySource source
    )
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        SigningKeyEntry[] entries = [.. keys];
        if (Array.Exists(entries, entry => entry is null))
        {
            throw new ArgumentException("A snapshot cannot contain a null key.", nameof(keys));
        }

        _entries = entries;
        Keys = entries.AsReadOnly();
        RetrievedAt = retrievedAt;
        RetrievedAtTimestamp = retrievedAtTimestamp;
        Version = version;
        Source = source;
    }

    /// <summary>The keys, in retrieval order.</summary>
    public IReadOnlyList<SigningKeyEntry> Keys { get; }

    /// <summary>
    /// Creates detached validation keys, one per entry and in the same order as <see cref="Keys"/>. Every call returns
    /// new instances, so a caller that changes them cannot affect the snapshot, its lookups, or its JWKS projection.
    /// </summary>
    public IReadOnlyList<SecurityKey> CreateSecurityKeys() =>
        Array.ConvertAll(_entries, entry => entry.CreateSecurityKey());

    /// <summary>When the retrieval that produced this snapshot completed, by the wall clock.</summary>
    public DateTimeOffset RetrievedAt { get; }

    /// <summary>
    /// The same instant as <see cref="RetrievedAt"/>, as a <see cref="TimeProvider.GetTimestamp"/> value of the
    /// provider that published the snapshot. It is meaningful only with that same <see cref="TimeProvider"/>.
    /// </summary>
    public long RetrievedAtTimestamp { get; }

    /// <summary>A positive number that increases with each published snapshot.</summary>
    public long Version { get; }

    public SigningKeySource Source { get; }

    /// <summary>
    /// Whether a key with this id is present. The comparison is ordinal: a <c>kid</c> is an opaque identifier.
    /// </summary>
    public bool ContainsKeyId(string keyId) =>
        Keys.Any(entry => string.Equals(entry.KeyId, keyId, StringComparison.Ordinal));

    /// <summary>
    /// The snapshot's age: the larger of its wall-clock age and the monotonic time elapsed since
    /// <see cref="RetrievedAtTimestamp"/>. It is never less than the monotonic elapsed time, so setting the wall clock
    /// back cannot make the snapshot younger. A wall clock set forward can make it older until the step is reversed.
    /// <paramref name="timeProvider"/> must be the one that published the snapshot.
    /// </summary>
    public TimeSpan GetAge(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        TimeSpan wallAge = timeProvider.GetUtcNow() - RetrievedAt;
        TimeSpan monotonicAge = timeProvider.GetElapsedTime(RetrievedAtTimestamp);
        return wallAge > monotonicAge ? wallAge : monotonicAge;
    }

    /// <summary>
    /// Classifies a snapshot of the given <paramref name="age"/> (see <see cref="GetAge"/>). An age equal to a bound
    /// belongs to the more usable state: exactly <see cref="SigningKeySettings.RefreshInterval"/> old is still fresh,
    /// and exactly <see cref="SigningKeySettings.MaxStaleness"/> old is still overdue (served).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="age"/> is negative.</exception>
    public static SigningKeySnapshotState GetState(TimeSpan age, SigningKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(age, TimeSpan.Zero);

        if (age <= settings.RefreshInterval)
        {
            return SigningKeySnapshotState.Fresh;
        }

        return age <= settings.MaxStaleness
            ? SigningKeySnapshotState.Overdue
            : SigningKeySnapshotState.Expired;
    }
}
