// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Invokes the plugin service-contribution phase and registers what the startup checks will read.
/// </summary>
/// <remarks>
/// Deliberately free of any dependency on Core beyond <see cref="IDmsStartupTask"/>: the plugin
/// checks read per-plugin records that <c>EdFi.Api.Plugins.Hosting</c> produces, and a project
/// reference from Core into that tree would invert the dependency and drag the plugin contract into
/// every Core consumer.
/// </remarks>
public static class PluginCompositionServiceExtensions
{
    /// <summary>
    /// Runs the plugin service-contribution phase and registers the audit input and the startup check.
    /// </summary>
    /// <remarks>
    /// Called once and unconditionally, so a deployment with no plugins takes exactly the same path as
    /// one with plugins and the check is never absent because nothing was allowlisted. The registry the
    /// invoker is given is <see cref="DmsPluginContracts.Registry"/>, the same instance the bootstrap
    /// phase derives the loader's contract assembly names from.
    /// </remarks>
    public static IServiceCollection AddPluginServiceContributions(
        this IServiceCollection services,
        IConfiguration configuration,
        LoadedPlugins plugins
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plugins);

        return services.AddPluginAudit(
            plugins.ContributeServices(services, configuration, DmsPluginContracts.Registry)
        );
    }

    /// <summary>
    /// Registers the audit input the contribution phase produced, and the startup check that reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only place these two registrations exist, so nothing can duplicate the ordering they depend
    /// on. Both halves matter. The input is registered as an <em>instance</em>, so the container
    /// activates nothing to hand it over; and it is registered <em>after</em> every plugin hook has
    /// already run, so a plugin that registered its own cannot displace it: a single-service resolve
    /// takes the last registration for a service type, and this is the last one.
    /// </para>
    /// <para>
    /// Nothing here registers <see cref="IServiceCollection"/>. The check takes its input by
    /// constructor and never reads the collection out of the container, which a plugin is permitted to
    /// register its own of.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPluginAudit(
        this IServiceCollection services,
        PluginAuditInput auditInput
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(auditInput);

        services.AddSingleton(auditInput);
        services.AddSingleton<IDmsStartupTask, PluginRegistrationGuard>();

        return services;
    }
}
