// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The structured reason a plugin failed to load, one value per fatal row the plugin design defines.
/// </summary>
/// <remarks>
/// The reason travels on <see cref="PluginLoadException"/> beside the human-readable message so that
/// a caller, and a test, can tell which rule fired without matching on message text. Every value is a
/// fatal: the design refuses skip-and-continue, because an allowlisted plugin that silently does not
/// load takes correctness or security posture with it.
/// </remarks>
public enum PluginLoadFailure
{
    /// <summary>A name appears more than once in the allowlist, before or after trimming.</summary>
    DuplicateAllowlistEntry,

    /// <summary>An allowlist entry is not a single path segment of the permitted shape.</summary>
    InvalidAllowlistName,

    /// <summary>The configured plugin directory could not be turned into a full path.</summary>
    PluginPathUnresolvable,

    /// <summary>The plugin root does not exist and the allowlist is not empty.</summary>
    PluginRootMissing,

    /// <summary>An allowlisted directory does not exist under the plugin root.</summary>
    PluginDirectoryMissing,

    /// <summary>An allowlisted directory resolves outside the plugin root once links are followed.</summary>
    EscapesPluginRoot,

    /// <summary>The entry assembly named for the plugin directory is not present.</summary>
    EntryAssemblyMissing,

    /// <summary>The dependency manifest named for the plugin directory is not present.</summary>
    DepsJsonMissing,

    /// <summary>The dependency manifest is not readable as the shape a publish produces.</summary>
    DepsJsonUnreadable,

    /// <summary>The dependency manifest declares a runtime pack, which a self-contained publish produces.</summary>
    SelfContainedPublish,

    /// <summary>The entry assembly references a newer version of a declared contract than the host carries.</summary>
    ContractVersionSkew,

    /// <summary>The entry assembly references a declared contract assembly the host cannot resolve.</summary>
    ContractAssemblyMissing,

    /// <summary>The manifest declares a higher version of an assembly the host also carries.</summary>
    DependencyVersionSkew,

    /// <summary>The entry assembly is not a readable managed assembly.</summary>
    EntryAssemblyUnreadable,

    /// <summary>The entry assembly could not be loaded into the plugin's context.</summary>
    EntryAssemblyLoadFailed,

    /// <summary>The entry assembly's exported types could not be enumerated.</summary>
    PluginTypesUnloadable,

    /// <summary>The entry assembly exposes no constructible plugin type.</summary>
    NoPluginType,

    /// <summary>The entry assembly exposes more than one constructible plugin type.</summary>
    MultiplePluginTypes,

    /// <summary>The entry assembly's own name does not equal the plugin directory name.</summary>
    AssemblyNameMismatch,

    /// <summary>The plugin type could not be constructed.</summary>
    PluginActivationFailed,

    /// <summary>The plugin's name could not be read.</summary>
    PluginNameUnavailable,

    /// <summary>The plugin's name does not equal the plugin directory name.</summary>
    PluginNameMismatch,

    /// <summary>A file the manifest declares is present but could not be read.</summary>
    PluginFileUnreadable,
}
