// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// What a third party writes: a subclass of the packaged contract, compiled against the packed nupkg
// rather than against anything in this repository. If the contract packed a signature without the
// dependency closure that signature needs, this project fails to compile.
//
// The region below is mirrored verbatim into the packed readme, and a check compares the two, so the
// sample an implementer copies is one that has been compiled. It therefore carries its own usings,
// its own namespace, and every type it names: a sample that leaned on something declared outside it
// would not compile where it is read.
//
// This project is a compile check and is never loaded by a host, so its assembly name is
// PluginsConsumer and deliberately does not equal the sample's Name. A real plugin must make the
// directory name, the assembly name, and Name one string; the readme shows the project settings that
// do that.

// embed-region: sample
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Dms.Sample;

public sealed class AcmeSamplePlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Dms.Sample";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new AcmeEndpoint(configuration["Acme:Endpoint"] ?? "https://localhost"));
    }
}

internal sealed class AcmeEndpoint(string address)
{
    public string Address { get; } = address;
}
// embed-region-end: sample
