// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PluginsConsumer;

/// <summary>
/// What a third party writes: a subclass of the packaged contract, compiled against the packed
/// nupkg rather than against anything in this repository.
/// </summary>
/// <remarks>
/// Both members are overridden deliberately. <c>Name</c> is abstract, so the compiler would
/// demand it in any case; <c>ContributeServices</c> is virtual, and overriding it is what proves
/// the hook is reachable and that both parameter types resolve for an outside project. The body
/// registers a service and reads a configuration value rather than being empty, so a contract that
/// packed the signature without its dependency closure would fail to compile here.
/// </remarks>
internal sealed class AcmePlugin : EdFiApiPlugin
{
    public override string Name => "acme";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAcmeEndpoint>(new AcmeEndpoint(configuration["Acme:Endpoint"] ?? "https://localhost"));
    }
}

internal interface IAcmeEndpoint
{
    string Address { get; }
}

internal sealed class AcmeEndpoint(string address) : IAcmeEndpoint
{
    public string Address { get; } = address;
}
