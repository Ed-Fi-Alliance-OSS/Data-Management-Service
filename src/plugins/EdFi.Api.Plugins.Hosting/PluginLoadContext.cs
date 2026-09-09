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
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, Version> _declaredVersions;
    private readonly Lock _substitutionsLock = new();
    private readonly Dictionary<string, HostFirstSubstitution> _substitutions = new(StringComparer.Ordinal);

    internal PluginLoadContext(string pluginName, string entryAssemblyPath)
        : this(pluginName, entryAssemblyPath, new Dictionary<string, Version>(StringComparer.Ordinal)) { }

    internal PluginLoadContext(
        string pluginName,
        string entryAssemblyPath,
        IReadOnlyDictionary<string, Version> declaredVersions
    )
        : base(pluginName, isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _declaredVersions = declaredVersions;
    }

    /// <summary>
    /// Every assembly this context has actually served from the host in place of the plugin's own,
    /// where what the host carries is not what the plugin's manifest declared.
    /// </summary>
    /// <remarks>
    /// Read when the caller asks rather than frozen when loading finished: <c>Load</c> is called when a
    /// type resolution first needs an assembly, so an assembly first touched inside a contribution hook
    /// is served long after the loader's own work is over. These are resolutions that happened, not
    /// predictions about resolutions that might.
    /// </remarks>
    internal IReadOnlyList<HostFirstSubstitution> MaterializeSubstitutions()
    {
        lock (_substitutionsLock)
        {
            return [.. _substitutions.Values];
        }
    }

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

        Version? hostVersion = hostAssembly.GetName().Version;

        if (assemblyName.Version is { } requested && hostVersion is not null && hostVersion < requested)
        {
            // Name is the base class's own copy of pluginName. Reading a constructor parameter here
            // instead would capture it a second time, which is CS9107 and therefore an error under this
            // repository's TreatWarningsAsErrors.
            throw new PluginVersionSkewException(Name!, simpleName, requested, hostVersion);
        }

        if (hostVersion is not null)
        {
            RecordSubstitution(simpleName, assemblyName.Version, hostVersion);
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

    /// <summary>
    /// Records a resolution the host answered, when what it served differs from what the plugin
    /// declared or, absent a declaration, from what the reference asked for.
    /// </summary>
    private void RecordSubstitution(string simpleName, Version? requestedVersion, Version hostVersion)
    {
        Version? declaredVersion = _declaredVersions.GetValueOrDefault(simpleName);

        // The manifest declaration is the comparison that matters, because it is what the plugin
        // shipped and therefore what its author tested against. The reference version is a different
        // fact and is carried separately rather than standing in for the declaration: they can differ,
        // and a shared-framework assembly has no declaration at all.
        bool substituted = declaredVersion is not null
            ? declaredVersion != hostVersion
            : requestedVersion is not null && requestedVersion != hostVersion;

        if (!substituted)
        {
            return;
        }

        lock (_substitutionsLock)
        {
            _substitutions.TryAdd(
                simpleName,
                new HostFirstSubstitution(simpleName, requestedVersion, hostVersion, declaredVersion)
            );
        }
    }
}
