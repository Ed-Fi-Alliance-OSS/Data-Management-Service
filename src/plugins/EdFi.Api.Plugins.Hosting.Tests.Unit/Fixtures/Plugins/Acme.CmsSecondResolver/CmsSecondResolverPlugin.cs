// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.CmsSecondResolver;

/// <summary>
/// Registers one secret resolver and nothing else, so that beside another plugin doing the same the
/// replace-cardinality contract is claimed once by each of two plugins.
/// </summary>
public sealed class CmsSecondResolverPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.CmsSecondResolver";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<ISecretResolver, CmsSecondResolverSecretResolver>();
}

/// <summary>The plugin's secret resolver. Resolves nothing: only its registration is under test.</summary>
public sealed class CmsSecondResolverSecretResolver : ISecretResolver
{
    public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the fixture resolver is registered, never called");
}
