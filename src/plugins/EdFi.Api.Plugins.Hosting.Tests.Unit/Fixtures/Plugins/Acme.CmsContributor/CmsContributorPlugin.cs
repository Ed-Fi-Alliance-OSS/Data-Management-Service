// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Acme.CmsContributor;

/// <summary>
/// Exercises both composition phases against the Configuration Service: its configuration hook adds a
/// source supplying a value the host reads, and its service hook registers a secret resolver beside
/// its own service, the options that service reads, and a hosted service.
/// </summary>
/// <remarks>
/// What either hook does beyond that is chosen by <see cref="BehaviorKey"/> in the host's
/// configuration, so one published fixture serves every boot case. With the key absent both hooks do
/// only what is described above.
/// </remarks>
public sealed class CmsContributorPlugin : EdFiApiPlugin
{
    /// <summary>The host configuration key that picks a behavior. Named for this plugin alone.</summary>
    public const string BehaviorKey = "Fixture:Acme.CmsContributor:Behavior";

    /// <summary>The origin this plugin supplies for the key the host's CORS policy reads.</summary>
    public const string SwaggerUiOrigin = "https://acme-cms-contributor.example";

    /// <summary>
    /// A key a vault-backed source would plausibly supply. Nothing reads it: it exists so a test can
    /// assert neither it nor its value reaches any diagnostic channel.
    /// </summary>
    public const string SecretLookingKey = "AcmeVault:ClientSecret";

    /// <summary>The secret-looking value supplied under <see cref="SecretLookingKey"/>.</summary>
    public const string SecretLookingValue = "acme-vault-7f3c9e1b-plugin-secret";

    /// <summary>The plugin's own configuration section, which its options bind from.</summary>
    public const string OwnSectionName = "AcmeCmsContributor";

    /// <summary>The value this plugin's source supplies for its own options to bind.</summary>
    public const string GreetingValue = "greetings-from-acme-cms-contributor";

    /// <summary>
    /// A real Configuration Service service type the guard behaviors act on, named rather than
    /// referenced so the fixture does not compile against the host. Registered by the host's
    /// PostgreSQL branch before any plugin runs.
    /// </summary>
    public const string HostServiceTypeName = "EdFi.DmsConfigurationService.Backend.Deploy.IDatabaseDeploy";

    public override string Name => "Acme.CmsContributor";

    public override void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    )
    {
        if (bootstrapConfiguration[BehaviorKey] == "throwFromConfiguration")
        {
            throw new InvalidOperationException("the fixture configuration hook failed on purpose");
        }

        configurationBuilder.Add(new CmsContributorConfigurationSource());
    }

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        string? behavior = configuration[BehaviorKey];

        switch (behavior)
        {
            case "throwFromServices":
                throw new InvalidOperationException("the fixture service hook failed on purpose");

            case "decoyAudit":
                // A plugin's own audit input, registered before the host registers its own. The host's
                // is last, and a single-service resolve takes the last registration.
                services.AddSingleton(new PluginAuditInput(new PluginContractRegistry([]), [], []));
                break;

            case "claimHostService":
                // Adding is not the wrapper's to refuse; the audit's displacement check is.
                services.Add(
                    ServiceDescriptor.Singleton(FindHostDescriptor(services).ServiceType, _ => new object())
                );
                break;

            case "removeHostService":
                services.Remove(FindHostDescriptor(services));
                break;

            case "overwriteHostService":
                ServiceDescriptor existing = FindHostDescriptor(services);
                services[services.IndexOf(existing)] = ServiceDescriptor.Singleton(
                    existing.ServiceType,
                    _ => new object()
                );
                break;
        }

        AddSecretResolver(services, behavior);
        AddClientSecretHasher(services, behavior);

        // The plugin's own service and the options it reads, beside the declared contract.
        services.AddOptions<CmsContributorOptions>().Bind(configuration.GetSection(OwnSectionName));
        services.AddSingleton<CmsContributorGreeter>();
        services.AddHostedService<CmsContributorHostedService>();
    }

    /// <summary>
    /// The host's descriptor for <see cref="HostServiceTypeName"/>. Throws a failure of its own when
    /// the host no longer registers it, so a moved type cannot pass for a guard refusal.
    /// </summary>
    private static ServiceDescriptor FindHostDescriptor(IServiceCollection services) =>
        services.FirstOrDefault(descriptor => descriptor.ServiceType.FullName == HostServiceTypeName)
        ?? throw new InvalidOperationException(
            $"fixture setup: the host registers no '{HostServiceTypeName}' to act on"
        );

    /// <summary>
    /// The secret resolver, as a singleton unless the behavior asks for a shape the host refuses or for
    /// a second claim on the contract.
    /// </summary>
    private static void AddSecretResolver(IServiceCollection services, string? behavior)
    {
        switch (behavior)
        {
            case "scopedResolver":
                services.AddScoped<ISecretResolver, CmsContributorSecretResolver>();
                break;

            case "transientResolver":
                services.AddTransient<ISecretResolver, CmsContributorSecretResolver>();
                break;

            case "keyedResolver":
                services.AddKeyedSingleton<ISecretResolver, CmsContributorSecretResolver>("acme-vault");
                break;

            case "resolverTwice":
                services.AddSingleton<ISecretResolver, CmsContributorSecretResolver>();
                services.AddSingleton<ISecretResolver, CmsContributorSecretResolver>();
                break;

            default:
                services.AddSingleton<ISecretResolver, CmsContributorSecretResolver>();
                break;
        }
    }

    /// <summary>
    /// The client secret hasher, registered only when the behavior asks for one: once as a singleton,
    /// the shape the host accepts, or in a shape the host refuses. The default leaves the host's own
    /// hasher in place.
    /// </summary>
    private static void AddClientSecretHasher(IServiceCollection services, string? behavior)
    {
        switch (behavior)
        {
            case "singletonHasher":
                services.AddSingleton<IClientSecretHasher, CmsContributorClientSecretHasher>();
                break;

            case "scopedHasher":
                services.AddScoped<IClientSecretHasher, CmsContributorClientSecretHasher>();
                break;

            case "transientHasher":
                services.AddTransient<IClientSecretHasher, CmsContributorClientSecretHasher>();
                break;

            case "keyedHasher":
                services.AddKeyedSingleton<IClientSecretHasher, CmsContributorClientSecretHasher>(
                    "acme-hasher"
                );
                break;
        }
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
        Data[$"{CmsContributorPlugin.OwnSectionName}:Greeting"] = CmsContributorPlugin.GreetingValue;
    }
}

/// <summary>The plugin's own options, bound from its own section.</summary>
public sealed class CmsContributorOptions
{
    public string Greeting { get; set; } = "";
}

/// <summary>
/// The plugin's own service, which reads its options. Its string form is the bound value, so a test
/// that cannot name this type can still read what it resolved to.
/// </summary>
public sealed class CmsContributorGreeter(IOptions<CmsContributorOptions> options)
{
    public override string ToString() => options.Value.Greeting;
}

/// <summary>The plugin's secret resolver. Resolves nothing: no caller exists until the read seam.</summary>
public sealed class CmsContributorSecretResolver : ISecretResolver
{
    public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the fixture resolver is registered, never called");
}

/// <summary>The plugin's client secret hasher. Hashes nothing: only its registration is under test.</summary>
public sealed class CmsContributorClientSecretHasher : IClientSecretHasher
{
    public Task<string> HashSecretAsync(string plainTextSecret) =>
        throw new NotSupportedException("the fixture hasher is registered, never called");

    public Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret) =>
        throw new NotSupportedException("the fixture hasher is registered, never called");

    public bool IsSecretHashed(string secret) =>
        throw new NotSupportedException("the fixture hasher is registered, never called");
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
