// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Secrets;

namespace SecretsConsumer;

/// <summary>
/// An implementer-authored <see cref="ISecretResolver"/> that reads each secret from a mounted
/// file, the shape the secrets design's own end-to-end fixture takes. It reads the file on every
/// call rather than answering from state, which is the caching obligation the contract states:
/// the host caches values in front of the resolver, so the resolver never returns a value it did
/// not just fetch.
/// This is a verification fixture, compiled but never run.
/// </summary>
public sealed class MountedFileSecretResolver(string rootDirectory) : ISecretResolver
{
    public async ValueTask<string> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        // The tenant arrives on every call, never from ambient state, because one resolver
        // instance serves every tenant for the life of the process.
        string directory = reference.Tenant is null
            ? rootDirectory
            : Path.Combine(rootDirectory, reference.Tenant);

        string value = await File.ReadAllTextAsync(
            Path.Combine(directory, reference.Name),
            cancellationToken
        );

        return value.TrimEnd();
    }
}
