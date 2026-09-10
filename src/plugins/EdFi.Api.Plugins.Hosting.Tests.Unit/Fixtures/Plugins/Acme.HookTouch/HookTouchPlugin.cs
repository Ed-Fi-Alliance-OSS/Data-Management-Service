// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using Acme.HostShared;
using Acme.Private;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.HookTouch;

/// <summary>
/// Resolves two of its declared assemblies for the first time from inside its hook.
/// </summary>
/// <remarks>
/// Assemblies load lazily, so neither the private copy nor the host's substitute has been touched when
/// the loader finishes. A plugin reaching for a dependency while registering its services is ordinary,
/// and it is the moment that separates a record read now from one frozen earlier.
/// </remarks>
public sealed class HookTouchPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.HookTouch";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        FixtureObservations.Record("private", PrivateMarker.Describe());
        FixtureObservations.Record("hostShared", HostSharedMarker.Describe());

        services.AddSingleton<IAcmeFirstService, AcmeService>();
    }
}
