// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.Loader;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// One plugin the loader constructed, with what a caller needs to invoke and to report it.
/// </summary>
/// <remarks>
/// Read-only to everyone outside this assembly: the loader is the only thing that can produce one,
/// because every value here is a fact it established while loading. A record a caller could construct
/// would be a record a caller could construct wrongly, and the four equalities this type asserts by
/// existing would then mean nothing.
/// </remarks>
public sealed record LoadedPlugin
{
    internal LoadedPlugin(
        string name,
        EdFiApiPlugin instance,
        string directory,
        Version entryAssemblyVersion,
        AssemblyLoadContext loadContext
    )
    {
        Name = name;
        Instance = instance;
        Directory = directory;
        EntryAssemblyVersion = entryAssemblyVersion;
        LoadContext = loadContext;
    }

    /// <summary>
    /// The plugin's name, which equals its directory name, its entry assembly's file name, and the
    /// entry assembly's own name. All four are checked, so the loader never returns an instance for
    /// which they disagree.
    /// </summary>
    public string Name { get; }

    /// <summary>The single plugin type the entry assembly exposes, constructed.</summary>
    public EdFiApiPlugin Instance { get; }

    /// <summary>The plugin directory the entry assembly was loaded from, resolved.</summary>
    public string Directory { get; }

    /// <summary>
    /// The entry assembly's version, read from the loaded assembly rather than from the manifest,
    /// because a framework-dependent publish records no version for the project's own entry.
    /// </summary>
    public Version EntryAssemblyVersion { get; }

    /// <summary>
    /// The plugin's own load context, held so that later work can read from it at the moment it asks
    /// rather than from a snapshot taken when loading finished.
    /// </summary>
    internal AssemblyLoadContext LoadContext { get; }
}
