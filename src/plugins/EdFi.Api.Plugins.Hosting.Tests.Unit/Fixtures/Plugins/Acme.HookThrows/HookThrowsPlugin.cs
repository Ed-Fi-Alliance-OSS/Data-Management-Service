// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.HookThrows;

/// <summary>Fails inside its contribution hook, the way a plugin with a bad assumption would.</summary>
public sealed class HookThrowsPlugin : EdFiApiPlugin
{
    /// <summary>The message the host is expected to carry out unchanged, on the inner exception.</summary>
    public const string FailureMessage = "Acme.HookThrows could not read its own configuration";

    public override string Name => "Acme.HookThrows";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // Taken on entry, before anything else. A test compares it against the number the host's
        // diagnostic channel took when it announced this plugin, so an announcement written on the way
        // out of a catch block would order after this rather than before it.
        FixtureObservations.Record("enteredAt", FixtureObservations.Next().ToString());

        throw new InvalidOperationException(FailureMessage);
    }
}
