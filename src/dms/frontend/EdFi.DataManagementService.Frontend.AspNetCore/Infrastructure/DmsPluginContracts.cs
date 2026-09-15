// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.CustomValidation;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The plugin contracts the Data Management Service declares.
/// </summary>
/// <remarks>
/// <para>
/// A DMS-specific value on the DMS side of the seam. <see cref="PluginContractRegistry"/> itself is
/// host-agnostic and lives in the plugin hosting assembly, which the Configuration Service also
/// consumes; an instance naming DMS's own contracts would be a value that host inherits and cannot
/// use.
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
internal static class DmsPluginContracts
{
    /// <summary>
    /// The registry. One entry today: the custom resource validator, which is fan-in, so any number of
    /// plugins may contribute an implementation.
    /// </summary>
    public static PluginContractRegistry Registry { get; } =
        new([new PluginContractEntry(typeof(ICustomResourceValidator), Cardinality.FanIn)]);
}
