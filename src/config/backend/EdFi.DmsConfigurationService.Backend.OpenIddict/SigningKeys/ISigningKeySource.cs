// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// What one read of the key store produced: the usable entries, plus how many key records were read but could not be
/// used. The provider classifies it (spec D-4); a source never decides between an empty store and unusable records.
/// </summary>
public sealed record SigningKeySourceResult
{
    public SigningKeySourceResult(IReadOnlyList<SigningKeyEntry> entries, int discardedCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(discardedCount);

        Entries = entries;
        DiscardedCount = discardedCount;
    }

    public IReadOnlyList<SigningKeyEntry> Entries { get; }

    public int DiscardedCount { get; }
}

/// <summary>
/// Reads the public signing keys from one store (spec §4.3.4). A source throws when the store itself cannot be read;
/// a key record it cannot use is counted in <see cref="SigningKeySourceResult.DiscardedCount"/> instead.
/// </summary>
public interface ISigningKeySource
{
    SigningKeySource Kind { get; }

    /// <summary>
    /// Reads the keys. <paramref name="cancellationToken"/> carries the provider's load deadline and host shutdown and
    /// must reach the store operation.
    /// </summary>
    Task<SigningKeySourceResult> LoadAsync(CancellationToken cancellationToken);
}
