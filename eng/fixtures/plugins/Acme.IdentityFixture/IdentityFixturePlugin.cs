// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.IdentityFixture;

public sealed class IdentityFixturePlugin : EdFiApiPlugin
{
    // Must equal the name of the directory this plugin is published into and named by in
    // Plugins:Allowed. The host treats a mismatch as fatal.
    public override string Name => "Acme.IdentityFixture";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentityFixtureOptions>(
            configuration.GetSection(IdentityFixtureOptions.SectionName)
        );

        // The in-memory stores and the policy source outlive a request, so they are singletons; the
        // dependency the provider takes is scoped, which is what the lifetime proof reads.
        services.AddSingleton<FixtureState>();
        services.AddSingleton<FixtureControlChannel>();
        services.AddSingleton<FixtureControlEvents>();
        services.AddSingleton<FixturePolicySource>();
        services.AddSingleton<FixtureActivationGate>();
        services.AddScoped<FixtureRequestScope>();

        // Removing the client's default loggers keeps request URLs, which carry client ids and
        // tenants, out of the host's logs.
        services
            .AddHttpClient(FixtureControlChannel.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        // A plain Add, never TryAdd, Replace or RemoveAll: a plugin claims the identity slot by
        // adding its implementation, and the host decides what to do when more than one plugin
        // claims it. A plugin that quietly displaced another registration would defeat that check.
        // The registration shape follows ThrowAt, which is read here because the shape is what the
        // variant exercises: an activation that fails in the factory, or in the constructor.
        FixtureThrowAt throwAt =
            configuration
                .GetSection(IdentityFixtureOptions.SectionName)
                .GetValue<FixtureThrowAt?>(nameof(IdentityFixtureOptions.ThrowAt))
            ?? FixtureThrowAt.None;

        switch (throwAt)
        {
            case FixtureThrowAt.Factory:
                services.AddScoped<IIdentityService>(provider =>
                {
                    provider.GetRequiredService<FixtureActivationGate>().Enter("factory");
                    return ActivatorUtilities.CreateInstance<FixtureIdentityService>(provider);
                });
                break;
            case FixtureThrowAt.Constructor:
                services.AddScoped<IIdentityService, FixtureGatedIdentityService>();
                break;
            default:
                services.AddScoped<IIdentityService, FixtureIdentityService>();
                break;
        }
    }
}
