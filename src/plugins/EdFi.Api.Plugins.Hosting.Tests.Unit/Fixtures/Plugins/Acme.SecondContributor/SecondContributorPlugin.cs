// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using EdFi.Api.Plugins;
using EdFi.DataManagementService.FixtureHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.SecondContributor;

/// <summary>
/// A second plugin identity. It reads the same behaviour key as the first, so a case can put two
/// plugins in the same situation and assert both are named.
/// </summary>
public sealed class SecondContributorPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.SecondContributor";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        switch (configuration["Fixture:Behavior"])
        {
            case "replaceClaim":
            case "replaceClaimTwice":
                // The same single claim either way: what differs between those two cases is whether the
                // first plugin claimed once or twice.
                services.Add(
                    ServiceDescriptor.Singleton<IFixtureReplaceContract, SecondPluginReplaceClaim>()
                );
                break;

            case "declaredValidatorOnly":
            // The healthy half of the two-plugin fan-in pair, and the case where a host descriptor is
            // the broken one. Both need this plugin's contribution to be constructible.
            case "fanInPair":
            case "fanInPairFactory":
            case "healthyFanInBesideBrokenHostDefault":
                services.TryAddEnumerable(
                    ServiceDescriptor.Transient<IFixtureFanInContract, SecondPluginFanIn>()
                );
                break;

            default:
                services.AddSingleton<IAcmeThirdService, AcmeService>();
                break;
        }
    }
}

/// <summary>This plugin's claim on the replace-cardinality contract.</summary>
public sealed class SecondPluginReplaceClaim : IFixtureReplaceContract
{
    public string Describe() => nameof(SecondPluginReplaceClaim);
}

/// <summary>This plugin's implementation of the fan-in contract.</summary>
public sealed class SecondPluginFanIn : IFixtureFanInContract
{
    public SecondPluginFanIn() => FixtureObservations.Count("secondFanIn.constructed");

    public string Describe() => nameof(SecondPluginFanIn);
}
