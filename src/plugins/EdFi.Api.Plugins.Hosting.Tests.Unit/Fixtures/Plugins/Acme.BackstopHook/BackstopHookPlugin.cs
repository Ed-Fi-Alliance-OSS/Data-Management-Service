// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.HostShared;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.BackstopHook;

/// <summary>
/// Loads cleanly and then asks for the skewed assembly, so the refusal happens after the loader has
/// finished.
/// </summary>
public sealed class BackstopHookPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.BackstopHook";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // The first and only resolution of Acme.HostShared. Nothing above this frame belongs to the
        // loader, which is exactly what the test characterises.
        services.AddSingleton(new BackstopHookMarker(HostSharedMarker.Describe()));
    }
}

/// <summary>What the hook would register if the resolution succeeded, which it does not.</summary>
public sealed record BackstopHookMarker(string Described);
