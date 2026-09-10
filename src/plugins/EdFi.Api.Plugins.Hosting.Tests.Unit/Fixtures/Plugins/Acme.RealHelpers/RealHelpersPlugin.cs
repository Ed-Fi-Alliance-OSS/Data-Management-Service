// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Azure.Core;
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Acme.RealHelpers;

/// <summary>
/// Registers its services the way a real plugin would, through the helpers the framework and a vendor
/// SDK actually ship.
/// </summary>
/// <remarks>
/// Nothing here is contrived to be safe. It is the ordinary registration work a plugin does: an HTTP
/// client, options bound from the host's configuration, logging, a cloud client, and the contract the
/// host declared. Whether the recording collection lets all of that through is the question, and a
/// fixture imitating those helpers could not ask it.
/// </remarks>
public sealed class RealHelpersPlugin : EdFiApiPlugin
{
    /// <summary>The named client this plugin registers, so a test can look for that name.</summary>
    public const string HttpClientName = "acme-real-helpers";

    /// <summary>The client name the cloud registration uses, for the same reason.</summary>
    public const string AzureClientName = "acme-blobs";

    public override string Name => "Acme.RealHelpers";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // The real HTTP client factory helper, in its named form, and configured, which is what a
        // plugin talking to its own service registers. Configuring it is not decoration: the host has
        // already called AddHttpClient itself, so the factory registrations this call makes are all
        // TryAdds that decline, and configuring the named client is the part that actually lands.
        services
            .AddHttpClient(HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));

        // The real options builder, bound to a section of the host's own configuration rather than to
        // one the plugin built, because binding what the host passed in is the case that matters.
        services.AddOptions<RealHelpersOptions>().Bind(configuration.GetSection("Acme"));

        // The real logging helper. Adding is untouched by the carve-out; only removing the host's
        // providers is refused.
        services.AddLogging();

        // The real vendor helper. AddAzureClients registers the factory infrastructure and the named
        // client below through Microsoft.Extensions.Azure itself.
        services.AddAzureClients(builder =>
        {
            builder
                .AddClient<AcmeBlobsClient, AcmeBlobsClientOptions>(
                    (options, _, _) => new AcmeBlobsClient(options)
                )
                .WithName(AzureClientName);
        });

        // And the contract the host declared, so this plugin has contributed something the host will
        // call and the startup checks admit it.
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, RealHelpersValidator>()
        );
    }
}

/// <summary>Options of the plugin's own, bound from the host's configuration.</summary>
public sealed class RealHelpersOptions
{
    public string Greeting { get; set; } = string.Empty;

    public int Retries { get; set; }
}

/// <summary>Options for the cloud client, in the shape the vendor helper requires.</summary>
public sealed class AcmeBlobsClientOptions : ClientOptions;

/// <summary>
/// A cloud client of the plugin's own, registered through the vendor's own factory helper.
/// </summary>
/// <remarks>
/// A client of this plugin's rather than a real storage client, so the fixture's closure stays at the
/// vendor's registration helper and does not pull in a service SDK. What is under test is the helper
/// and the registrations it makes, not the client's protocol.
/// </remarks>
public sealed class AcmeBlobsClient(AcmeBlobsClientOptions options)
{
    public AcmeBlobsClientOptions Options { get; } = options;
}

/// <summary>A validator of the plugin's own, against the real contract.</summary>
public sealed class RealHelpersValidator : ICustomResourceValidator
{
    public IReadOnlyList<ValidatedResource> AppliesTo { get; } = [new ValidatedResource("Ed-Fi", "School")];

    public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
}
