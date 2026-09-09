// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.SecondContributor;

/// <summary>Registers one service type no other fixture plugin registers.</summary>
public sealed class SecondContributorPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.SecondContributor";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAcmeThirdService, AcmeService>();
    }
}
