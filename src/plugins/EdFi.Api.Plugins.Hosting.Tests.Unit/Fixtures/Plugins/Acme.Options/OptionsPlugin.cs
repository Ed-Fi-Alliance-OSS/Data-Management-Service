// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Acme.Options;

/// <summary>
/// Binds its own options from the host's configuration, over defaults it composes itself.
/// </summary>
public sealed class OptionsPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Options";

    /// <summary>
    /// The identity of <see cref="IChangeToken"/> as this plugin's own assembly resolves it, which is
    /// the type the split would duplicate.
    /// </summary>
    public static Type ChangeTokenType() => typeof(IChangeToken);

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // Building a configuration of its own is what puts this plugin's copies of
        // Microsoft.Extensions.Configuration and Microsoft.Extensions.Primitives to work rather than
        // merely on disk: a provider it constructs has to satisfy the IConfiguration the host passed
        // in, and IConfiguration.GetReloadToken returns IChangeToken. A plugin composing its own
        // defaults is also ordinary, so this is not a contrivance built to break.
        IConfiguration defaults = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Greeting"] = "from the plugin's own defaults",
                    ["Retries"] = "3",
                }
            )
            .Build();

        services.Configure<AcmeOptions>(defaults);

        // Then the host's own section, which layers over those defaults. Configure binds through
        // Microsoft.Extensions.Options.ConfigurationExtensions and registers a change-token source
        // whose GetChangeToken returns the IChangeToken of whichever Primitives this plugin resolved.
        services.Configure<AcmeOptions>(configuration.GetSection("Acme"));
    }
}

/// <summary>The options this plugin binds, which is a plugin type the host never names.</summary>
public sealed class AcmeOptions
{
    public string Greeting { get; set; } = string.Empty;

    public int Retries { get; set; }
}
