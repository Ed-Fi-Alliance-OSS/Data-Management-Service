// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.Shared;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.LibV1;

/// <summary>
/// Reports, and registers, whatever version of Acme.Shared this plugin was actually served.
/// </summary>
public sealed class LibV1Plugin : EdFiApiPlugin
{
    public override string Name => "Acme.LibV1";

    /// <summary>Reads the private copy, which is what forces it to be resolved.</summary>
    public static string DescribeSharedDependency() => SharedMarker.Describe();

    /// <summary>
    /// The private copy's own type, so a test can compare identities rather than names.
    /// </summary>
    public static Type SharedMarkerType() => typeof(SharedMarker);

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // Registering through the hook rather than answering a static call is the half of the claim
        // that matters to a host: both plugins contribute to one container, and each contribution
        // carries the version its own copy reports.
        services.AddSingleton(new LibV1Marker(Name, SharedMarker.Describe()));
    }
}

/// <summary>What <see cref="LibV1Plugin"/> registers, named for the version its copy declares.</summary>
public sealed record LibV1Marker(string PluginName, string SharedVersion);
