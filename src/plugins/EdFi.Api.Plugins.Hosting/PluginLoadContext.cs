// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// One plugin's assembly load context: named for the plugin, non-collectible, resolving host-first.
/// </summary>
/// <remarks>
/// <para>
/// Host-first means any assembly the host itself carries is served from the default context, and the
/// plugin's own copy is used only for assemblies the host does not have. That is what keeps every type
/// the host and a plugin exchange to one identity, and it is why a plugin cannot silently displace a
/// host assembly. What a plugin still gets its own copy of is anything the host does not carry, which
/// is where real collisions live.
/// </para>
/// <para>
/// Non-collectible because nothing unloads a plugin.
/// </para>
/// </remarks>
internal sealed class PluginLoadContext(string pluginName, string entryAssemblyPath)
    : AssemblyLoadContext(pluginName, isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } simpleName)
        {
            return null;
        }

        // Asked by simple name so that the default binder's own version check cannot turn "the host
        // carries an older copy" into "the host does not have it": those two throw the same exception
        // and need opposite treatment.
        if (!HostAssemblies.TryLoad(simpleName, out Assembly hostAssembly))
        {
            // Genuinely plugin-private: the host carries nothing by this name.
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        if (
            assemblyName.Version is { } requested
            && hostAssembly.GetName().Version is { } hostVersion
            && hostVersion < requested
        )
        {
            // Name is the base class's own copy of pluginName. Reading the primary-constructor
            // parameter here instead would capture it a second time, which is CS9107 and therefore an
            // error under this repository's TreatWarningsAsErrors.
            throw new PluginVersionSkewException(Name!, simpleName, requested, hostVersion);
        }

        return hostAssembly;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // The resolver answers with a path and the override has to return a handle, so the path goes
        // through LoadUnmanagedDllFromPath. Returning zero is how the override says "fall back to the
        // default probing", which is what happens for a library the plugin did not ship.
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
