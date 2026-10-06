// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Secrets;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The plugin contracts the Configuration Service declares.
/// </summary>
/// <remarks>
/// <para>
/// A CMS-specific value on the CMS side of the seam. <see cref="PluginContractRegistry"/> itself is
/// host-agnostic and lives in the plugin hosting assembly, which the Data Management Service also
/// consumes; that host holds its own registry and cannot use this one.
/// </para>
/// <para>
/// Two callers, which is why this is a named static rather than a private field: the service
/// contribution phase passes the registry to the invoker, and the bootstrap phase passes
/// <see cref="PluginContractRegistry.ContractAssemblyNames"/> to the loader for its
/// newer-plugin-on-older-host preflight. Those names are derived by the registry from the contracts
/// below rather than written out a second time, so declaring a contract here extends the preflight
/// with no change to the loader.
/// </para>
/// </remarks>
internal static class CmsPluginContracts
{
    /// <summary>
    /// The registry. Two entries, both replace, so at most one plugin may supply each. The client
    /// secret hasher has a host default, which a plugin's implementation takes the place of. The secret
    /// resolver has none: without a plugin, nothing is registered for it.
    /// </summary>
    public static PluginContractRegistry Registry { get; } =
        new([
            new PluginContractEntry(typeof(ISecretResolver), Cardinality.Replace),
            new PluginContractEntry(typeof(IClientSecretHasher), Cardinality.Replace),
        ]);
}
