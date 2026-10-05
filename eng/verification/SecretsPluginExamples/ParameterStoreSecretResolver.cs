// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The region below is mirrored verbatim into the EdFi.Api.Secrets readme, and a check compares the
// two, so the example an implementer copies is one that has been compiled. It therefore carries its
// own usings, its own namespace, and every type it names.
//
// This project is a compile check and is never loaded by a host. It holds three plugin classes, which
// a real plugin assembly may not (the host requires exactly one), and its assembly name equals none of
// their Names. A real plugin is its own project whose directory name, assembly name, and Name are one
// string; PLUGINS.md shows the project settings that do that.

// embed-region: resolver
using Amazon.Extensions.NETCore.Setup;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using EdFi.Api.Plugins;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Cms.ParameterStoreSecrets;

public sealed class ParameterStoreSecretsPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Cms.ParameterStoreSecrets";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        string root =
            configuration["Acme:ParameterStore:SecretsRoot"]
            ?? throw new InvalidOperationException(
                "Set Acme:ParameterStore:SecretsRoot to the path secret references resolve under, such as /edfi/cms/secrets."
            );
        bool perTenant = string.Equals(
            configuration["Acme:ParameterStore:PerTenant"],
            "true",
            StringComparison.OrdinalIgnoreCase
        );

        // The client is the expensive thing to build, so it is built once and kept for the life of
        // the process; it caches its connection and its ambient credential itself. The host never
        // disposes it.
        IAmazonSimpleSystemsManagement client = configuration
            .GetAWSOptions()
            .CreateServiceClient<IAmazonSimpleSystemsManagement>();

        // A plain Add of one singleton, unkeyed instance. ISecretResolver is a replace contract, so
        // there is nothing to try, and a TryAdd would hide a second claimant instead of failing.
        services.AddSingleton<ISecretResolver>(new ParameterStoreSecretResolver(client, root, perTenant));
    }
}

/// <summary>
/// Maps a secret reference to a Parameter Store name and fetches it on every call. It caches the
/// client and never a value: the Configuration Service caches values in front of it, and the
/// rotation window an operator configures is the host's.
/// </summary>
public sealed class ParameterStoreSecretResolver(
    IAmazonSimpleSystemsManagement client,
    string root,
    bool perTenant
) : ISecretResolver
{
    private readonly string _root = root.TrimEnd('/');

    // Safe to call concurrently: it holds no mutable state, and the SDK client is thread-safe.
    public async ValueTask<string> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        GetParameterResponse response = await client.GetParameterAsync(
            new GetParameterRequest { Name = ParameterName(reference), WithDecryption = true },
            cancellationToken
        );

        // The host logs the type of an exception a resolver throws and never its message, so the
        // type is the diagnostic.
        return response.Parameter?.Value ?? throw new ParameterHasNoValueException();
    }

    // ${secret:prod/dms/ds-2026} resolves to <root>/<tenant>/prod/dms/ds-2026 when the deployment
    // keeps one subtree per tenant, and to <root>/prod/dms/ds-2026 otherwise. A tenant-agnostic
    // deployment, and every read in a single-tenant one, where the tenant is null, ignores the tenant.
    private string ParameterName(SecretReference reference)
    {
        string name = reference.Name.TrimStart('/');

        return perTenant && reference.Tenant is { } tenant ? $"{_root}/{tenant}/{name}" : $"{_root}/{name}";
    }
}

public sealed class ParameterHasNoValueException() : Exception("Parameter Store returned no value.");
// embed-region-end: resolver
