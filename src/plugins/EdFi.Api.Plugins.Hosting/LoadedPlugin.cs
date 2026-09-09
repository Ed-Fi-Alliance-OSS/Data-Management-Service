// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

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
    private readonly PluginLoadContext _loadContext;

    internal LoadedPlugin(
        string name,
        EdFiApiPlugin instance,
        string directory,
        Version entryAssemblyVersion,
        string entryAssemblyFileName,
        IReadOnlyList<PluginDeclaredFile> declaredFiles,
        PluginLoadContext loadContext
    )
    {
        Name = name;
        Instance = instance;
        Directory = directory;
        EntryAssemblyVersion = entryAssemblyVersion;
        EntryAssemblyFileName = entryAssemblyFileName;
        DeclaredFiles = declaredFiles;
        _loadContext = loadContext;
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

    /// <summary>The entry assembly's file name, which is how its inventory row is recognised.</summary>
    public string EntryAssemblyFileName { get; }

    /// <summary>
    /// Every file the plugin's dependency manifest declares, with its digest where it is present.
    /// </summary>
    /// <remarks>
    /// Fixed when the plugin loaded, because it comes from the declaration and from bytes sitting in a
    /// directory nothing may write. What is not fixed is whether each file has since been loaded, which
    /// is why the load state lives on <see cref="MaterializeInventory"/> instead.
    /// </remarks>
    public IReadOnlyList<PluginDeclaredFile> DeclaredFiles { get; }

    /// <summary>
    /// The inventory as it stands now, with each file's load state read from the plugin's own context
    /// at this moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load state is read here rather than stored, and the difference is the whole point. An
    /// assembly first touched inside a contribution hook loads after the loader's work is over, so a
    /// flag captured at the end of loading would report a file as unloaded while the process was
    /// already running it.
    /// </para>
    /// <para>
    /// A managed row is <see cref="PluginFileLoadState.Loaded"/> only when that file's own bytes are in
    /// the plugin's context. An assembly the host served instead reports
    /// <see cref="PluginFileLoadState.NotLoaded"/>, which is the truthful answer: the plugin's copy was
    /// never used, and <see cref="MaterializeSubstitutions"/> is where that shows up. A native row is
    /// <see cref="PluginFileLoadState.Unknown"/>, because the runtime exposes no way to ask.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PluginInventoryRow> MaterializeInventory()
    {
        // Compared ordinally, like every other comparison in this loader, and normalized first because
        // one side comes from a path this loader composed and the other from what the runtime recorded.
        HashSet<string> loadedLocations = new(
            _loadContext
                .Assemblies.Select(assembly => assembly.Location)
                .Where(location => location.Length > 0)
                .Select(Path.GetFullPath),
            StringComparer.Ordinal
        );

        List<PluginInventoryRow> rows = [];

        foreach (PluginDeclaredFile file in DeclaredFiles)
        {
            rows.Add(
                new PluginInventoryRow(
                    file.FileName,
                    file.DeclaredPath,
                    file.ResolvedRelativePath,
                    file.Kind,
                    file.DeclaredAssemblyVersion,
                    EffectiveVersionOf(file),
                    EffectiveVersionSourceOf(file),
                    file.Availability,
                    file.Sha256,
                    LoadStateOf(file, loadedLocations)
                )
            );
        }

        return rows;
    }

    /// <summary>
    /// Every assembly the host has actually served this plugin in place of its own, as it stands now.
    /// </summary>
    /// <remarks>
    /// Read at this moment for the same reason as the inventory: a substitution that happens inside a
    /// hook happens after loading finished. These are resolutions the context performed, never
    /// predictions about a dependency nothing has asked for yet.
    /// </remarks>
    public IReadOnlyList<HostFirstSubstitution> MaterializeSubstitutions() =>
        _loadContext.MaterializeSubstitutions();

    /// <summary>
    /// The version the process actually has for a file, which for the entry assembly comes from the
    /// loaded assembly because a framework-dependent publish declares none for it.
    /// </summary>
    private Version? EffectiveVersionOf(PluginDeclaredFile file) =>
        file.DeclaredAssemblyVersion ?? (IsEntryAssembly(file) ? EntryAssemblyVersion : null);

    private PluginVersionSource EffectiveVersionSourceOf(PluginDeclaredFile file)
    {
        if (file.DeclaredAssemblyVersion is not null)
        {
            return PluginVersionSource.DepsJsonDeclaration;
        }

        // The entry assembly is the one file a framework-dependent publish declares no version for, so
        // its effective version comes from the assembly the process loaded and says so.
        return IsEntryAssembly(file) ? PluginVersionSource.LoadedAssembly : PluginVersionSource.NotDeclared;
    }

    private bool IsEntryAssembly(PluginDeclaredFile file) =>
        string.Equals(file.FileName, EntryAssemblyFileName, StringComparison.Ordinal);

    private PluginFileLoadState LoadStateOf(PluginDeclaredFile file, HashSet<string> loadedLocations)
    {
        if (file.Kind == PluginFileKind.Native)
        {
            // The runtime exposes no way to enumerate the native libraries a context has resolved, so
            // reporting anything else here would be a claim nobody checked.
            return PluginFileLoadState.Unknown;
        }

        if (file.Availability == PluginFileAvailability.Absent)
        {
            return PluginFileLoadState.NotLoaded;
        }

        return loadedLocations.Contains(Path.GetFullPath(Path.Combine(Directory, file.ResolvedRelativePath!)))
            ? PluginFileLoadState.Loaded
            : PluginFileLoadState.NotLoaded;
    }
}
