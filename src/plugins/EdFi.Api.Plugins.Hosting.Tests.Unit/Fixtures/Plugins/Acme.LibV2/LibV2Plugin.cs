// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.Shared;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.LibV2;

/// <summary>
/// The same shape as Acme.LibV1, over the second major of the same private dependency.
/// </summary>
public sealed class LibV2Plugin : EdFiApiPlugin
{
    public override string Name => "Acme.LibV2";

    public static string DescribeSharedDependency() => SharedMarker.Describe();

    public static Type SharedMarkerType() => typeof(SharedMarker);

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LibV2Marker(Name, SharedMarker.Describe()));
    }
}

/// <summary>What <see cref="LibV2Plugin"/> registers, named for the version its copy declares.</summary>
public sealed record LibV2Marker(string PluginName, string SharedVersion);
