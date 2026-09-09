// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Good;

/// <summary>
/// A well-formed plugin. Its contribution is one marker registration, which is how a test tells that
/// this plugin's hook ran rather than another's.
/// </summary>
public sealed class GoodPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Good";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new GoodPluginMarker(Name));
    }
}

/// <summary>What <see cref="GoodPlugin"/> registers, so a test can resolve it and name the source.</summary>
public sealed record GoodPluginMarker(string PluginName);
