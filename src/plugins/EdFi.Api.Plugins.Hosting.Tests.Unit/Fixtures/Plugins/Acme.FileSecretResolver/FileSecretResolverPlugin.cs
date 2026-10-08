// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.Api.Plugins;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.FileSecretResolver;

public sealed class FileSecretResolverPlugin : EdFiApiPlugin
{
    /// <summary>The configuration key naming the secrets file, a JSON object of name to value.</summary>
    public const string PathKey = "AcmeFileSecrets:Path";

    public override string Name => "Acme.FileSecretResolver";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        string path =
            configuration[PathKey]
            ?? throw new InvalidOperationException($"{PathKey} must name the secrets file.");
        services.AddSingleton<ISecretResolver>(new FileSecretResolver(path));
    }
}

/// <summary>
/// Reads the secrets file on every call, so a value written to it is what the next call returns. The
/// tenant is part of the lookup: a value for a tenant is stored under <c>tenant/name</c>, and a
/// single-tenant value under the name alone.
/// </summary>
public sealed class FileSecretResolver(string path) : ISecretResolver
{
    public async ValueTask<string> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        await using FileStream stream = File.OpenRead(path);
        Dictionary<string, string> secrets =
            await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream,
                cancellationToken: cancellationToken
            ) ?? [];

        string key = reference.Tenant is null ? reference.Name : $"{reference.Tenant}/{reference.Name}";
        return secrets.TryGetValue(key, out string? value)
            ? value
            : throw new KeyNotFoundException($"The secrets file holds no value for '{key}'.");
    }
}
