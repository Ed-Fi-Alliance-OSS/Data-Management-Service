// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.OldContract;

/// <summary>
/// An ordinary plugin, written against contract 1.0.0 and knowing nothing of any later version.
/// </summary>
/// <remarks>
/// It overrides the one hook 1.0.0 declares. On a 1.1.0 host that override still has to run, which is
/// the compatibility direction the upgrade story needs, and the marker it registers is how the runner
/// reports that the hook really ran rather than merely resolved.
/// </remarks>
public sealed class OldContractPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.OldContract";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new OldContractMarker(Name));
    }
}

/// <summary>What the hook registers, so the runner can resolve it and say the hook ran.</summary>
public sealed record OldContractMarker(string PluginName);
