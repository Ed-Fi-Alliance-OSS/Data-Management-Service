// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Secrets;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// The registered hasher's hash of a random secret that nobody holds, which an unknown client's
/// presented secret is verified against, so an unknown client id costs the same single secret
/// verification as a known client with a wrong secret. One per container, shared by every token
/// manager registration. The host initializes it once at startup, before any request is served,
/// and refuses to start when the hasher cannot produce it; a token manager cannot be constructed
/// before then.
/// </summary>
public sealed class ClientSecretPlaceholderHash(IClientSecretHasher secretHasher)
{
    private string _hash = string.Empty;

    /// <summary>The placeholder hash, in the registered hasher's own stored form.</summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="InitializeAsync"/> has not completed successfully.
    /// </exception>
    public string Hash =>
        Volatile.Read(ref _hash) is { Length: > 0 } hash
            ? hash
            : throw new InvalidOperationException(
                "The placeholder client secret hash has not been initialized."
            );

    /// <summary>
    /// Hashes a random secret that is never stored or returned, through the registered hasher, so
    /// the placeholder has whatever format and work factor that hasher gives its own hashes. A
    /// throw, a faulted or cancelled task and an empty result all fail initialization and leave no
    /// placeholder.
    /// </summary>
    public async Task InitializeAsync()
    {
        string hash = await secretHasher.HashSecretAsync(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        );
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new InvalidOperationException("The client secret hasher returned an empty hash.");
        }

        Volatile.Write(ref _hash, hash);
    }
}
