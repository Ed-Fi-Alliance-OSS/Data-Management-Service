// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins;

/// <summary>
/// The plugin contract as a later major version would declare it. Only the shape matters here: a
/// plugin built against this records a reference to EdFi.Api.Plugins 2.0.0, which is what the
/// contract-skew check reads out of the entry assembly.
/// </summary>
public abstract class EdFiApiPlugin
{
    public abstract string Name { get; }

    public virtual void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // Intentionally does nothing, exactly as the shipped contract does.
    }
}
