// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// embed-region: plugin
using EdFi.Api.Plugins;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Dms.Identity;

public sealed class AcmeIdentityPlugin : EdFiApiPlugin
{
    // The plugin directory, the entry assembly's file name and its AssemblyName must all be exactly
    // this name.
    public override string Name => "Acme.Dms.Identity";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // The clients allowed to use this provider, read from the plugin's own configuration section.
        string[] authorizedClients =
        [
            .. configuration
                .GetSection("Acme:Identity:AuthorizedClients")
                .GetChildren()
                .Select(client => client.Value)
                .OfType<string>(),
        ];

        // One store serves every request, so it is a singleton.
        services.AddSingleton(new AcmeIdentityStore(authorizedClients));

        // A plain Add, unkeyed. The host registers its own default identity service, so a TryAdd would
        // be declined and the default would keep serving. A registration under a concrete key is never
        // used by a request, and one under the wildcard key stops the host at startup.
        // Scoped is one of the three supported lifetimes: this provider holds no state of its own.
        services.AddScoped<IIdentityService, AcmeIdentityService>();
    }
}
// embed-region-end: plugin
