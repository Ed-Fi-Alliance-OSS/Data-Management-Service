// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// What a loader warning is about.
/// </summary>
public enum PluginLoadWarningKind
{
    /// <summary>Directories under the plugin root that no allowlist entry names.</summary>
    UnallowlistedDirectories,

    /// <summary>
    /// The plugin root could not be listed, so whether it holds unallowlisted directories is unknown.
    /// </summary>
    PluginRootNotListed,
}

/// <summary>
/// Something the loader noticed that is worth an operator's attention and is not worth failing a boot
/// over.
/// </summary>
/// <remarks>
/// <para>
/// The loader necessarily runs before any logging pipeline exists, so it writes its own lines to
/// <see cref="Console.Error"/>. A deployment that collects application logs rather than container
/// stdout would lose them there, which is why each warning is also carried forward as data and
/// replayed through the host's real logger once one exists.
/// </para>
/// <para>
/// Metadata only: directory names and an exception message, never a path the host did not resolve and
/// never an object whose <c>ToString</c> the host does not control.
/// </para>
/// </remarks>
/// <param name="Kind">Which warning this is.</param>
/// <param name="Directories">
/// The directory names the warning is about, empty for a kind that names none.
/// </param>
/// <param name="Detail">The failure that produced the warning, or null where none did.</param>
public sealed record PluginLoadWarning(
    PluginLoadWarningKind Kind,
    IReadOnlyList<string> Directories,
    string? Detail
)
{
    /// <summary>Directories under the plugin root that no allowlist entry names.</summary>
    internal static PluginLoadWarning UnallowlistedDirectories(IReadOnlyList<string> directories) =>
        new(PluginLoadWarningKind.UnallowlistedDirectories, directories, null);

    /// <summary>The plugin root could not be listed.</summary>
    internal static PluginLoadWarning RootNotListed(string detail) =>
        new(PluginLoadWarningKind.PluginRootNotListed, [], detail);
}
