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

    /// <summary>Retrieved no more than <see cref="SigningKeySettings.RefreshInterval"/> ago.</summary>
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
    public SigningKeySnapshot(
        IEnumerable<SigningKeyEntry> keys,
        DateTimeOffset retrievedAt,
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

        Keys = entries.AsReadOnly();
        SecurityKeys = Array.ConvertAll(entries, entry => entry.SecurityKey).AsReadOnly();
        RetrievedAt = retrievedAt;
        Version = version;
        Source = source;
    }

    /// <summary>The keys, in retrieval order.</summary>
    public IReadOnlyList<SigningKeyEntry> Keys { get; }

    /// <summary>The keys as token validation consumes them, in the same order as <see cref="Keys"/>.</summary>
    public IReadOnlyList<SecurityKey> SecurityKeys { get; }

    /// <summary>When the retrieval that produced this snapshot completed.</summary>
    public DateTimeOffset RetrievedAt { get; }

    /// <summary>A positive number that increases with each published snapshot.</summary>
    public long Version { get; }

    public SigningKeySource Source { get; }

    /// <summary>
    /// Whether a key with this id is present. The comparison is ordinal: a <c>kid</c> is an opaque identifier.
    /// </summary>
    public bool ContainsKeyId(string keyId) =>
        Keys.Any(entry => string.Equals(entry.KeyId, keyId, StringComparison.Ordinal));

    /// <summary>
    /// Classifies the snapshot at <paramref name="now"/>. An age equal to a bound belongs to the more usable state:
    /// exactly <see cref="SigningKeySettings.RefreshInterval"/> old is still fresh, and exactly
    /// <see cref="SigningKeySettings.MaxStaleness"/> old is still overdue (served). A negative age, from a clock moved
    /// backwards, counts as fresh.
    /// </summary>
    public SigningKeySnapshotState GetState(DateTimeOffset now, SigningKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        TimeSpan age = now - RetrievedAt;
        if (age <= settings.RefreshInterval)
        {
            return SigningKeySnapshotState.Fresh;
        }

        return age <= settings.MaxStaleness
            ? SigningKeySnapshotState.Overdue
            : SigningKeySnapshotState.Expired;
    }
}
