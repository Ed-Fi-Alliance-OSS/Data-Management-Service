// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Every plugin the loader returned, in allowlist order.
/// </summary>
/// <remarks>
/// An aggregate rather than a bare list because the composition phases are added to it later: the
/// service contribution phase by the story that invokes hooks, and the configuration phase by the
/// secrets foundation story. Both need somewhere to live that a caller already holds.
/// </remarks>
public sealed class LoadedPlugins
{
    /// <summary>
    /// Creates an aggregate over the given plugins, in allowlist order. Internal because the loader is
    /// the only thing that has plugins to put in one.
    /// </summary>
    internal LoadedPlugins(IReadOnlyList<LoadedPlugin> plugins)
    {
        Plugins = plugins;
    }

    /// <summary>
    /// The value returned when no plugin was asked for, which is the shipped default.
    /// </summary>
    public static LoadedPlugins Empty { get; } = new([]);

    /// <summary>
    /// The loaded plugins, in the order the operator wrote them, which is the invocation order for
    /// both composition phases.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> Plugins { get; }
}
