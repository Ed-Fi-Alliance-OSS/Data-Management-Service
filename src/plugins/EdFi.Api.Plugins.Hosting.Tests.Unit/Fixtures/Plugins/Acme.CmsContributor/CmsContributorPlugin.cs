// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.CmsContributor;

/// <summary>
/// Exercises both composition phases against the Configuration Service: its configuration hook adds a
/// source supplying a value the host reads, and its service hook registers a secret resolver.
/// </summary>
public sealed class CmsContributorPlugin : EdFiApiPlugin
{
    /// <summary>The origin this plugin supplies for the key the host's CORS policy reads.</summary>
    public const string SwaggerUiOrigin = "https://acme-cms-contributor.example";

    /// <summary>
    /// A key a vault-backed source would plausibly supply. Nothing reads it: it exists so a test can
    /// assert neither it nor its value reaches any diagnostic channel.
    /// </summary>
    public const string SecretLookingKey = "AcmeVault:ClientSecret";

    /// <summary>The secret-looking value supplied under <see cref="SecretLookingKey"/>.</summary>
    public const string SecretLookingValue = "acme-vault-7f3c9e1b-plugin-secret";

    public override string Name => "Acme.CmsContributor";

    public override void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    ) => configurationBuilder.Add(new CmsContributorConfigurationSource());

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISecretResolver, CmsContributorSecretResolver>();
        services.AddHostedService<CmsContributorHostedService>();
    }
}

/// <summary>
/// The plugin's configuration source. A type of its own, so a test can recognize it by name.
/// </summary>
public sealed class CmsContributorConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new CmsContributorConfigurationProvider();
}

internal sealed class CmsContributorConfigurationProvider : ConfigurationProvider
{
    public override void Load()
    {
        Data["Cors:SwaggerUIOrigin"] = CmsContributorPlugin.SwaggerUiOrigin;
        Data[CmsContributorPlugin.SecretLookingKey] = CmsContributorPlugin.SecretLookingValue;
    }
}

/// <summary>The plugin's secret resolver. Resolves nothing: no caller exists until the read seam.</summary>
public sealed class CmsContributorSecretResolver : ISecretResolver
{
    public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the fixture resolver is registered, never called");
}

/// <summary>
/// A hosted service registered beside the contract, so the inventory has one to list. It does
/// nothing.
/// </summary>
public sealed class CmsContributorHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
