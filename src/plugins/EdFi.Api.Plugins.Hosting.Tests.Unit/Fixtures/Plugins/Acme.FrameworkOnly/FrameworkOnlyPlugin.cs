// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.FrameworkOnly;

/// <summary>
/// Names the hook signature's assemblies in its own public surface, taking them from the shared
/// framework rather than from packages.
/// </summary>
public sealed class FrameworkOnlyPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.FrameworkOnly";

    /// <summary>
    /// The identity of <see cref="IServiceCollection"/> as this plugin's own assembly resolves it.
    /// </summary>
    /// <remarks>
    /// A type reference in this assembly's metadata, resolved when the method runs, so the answer is
    /// the assembly the plugin's context actually served rather than the one the host happens to hold.
    /// </remarks>
    public static Type ServiceCollectionType() => typeof(IServiceCollection);

    /// <summary>The identity of <see cref="IConfiguration"/>, resolved the same way.</summary>
    public static Type ConfigurationType() => typeof(IConfiguration);

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new FrameworkOnlyMarker(Name, configuration.GetSection("Acme").Path));
    }
}

/// <summary>What <see cref="FrameworkOnlyPlugin"/> registers, so the hook is observably reached.</summary>
public sealed record FrameworkOnlyMarker(string PluginName, string SectionPath);
