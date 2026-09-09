// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>What kind of asset a plugin's dependency manifest declared.</summary>
public enum PluginFileKind
{
    /// <summary>A managed assembly.</summary>
    Managed,

    /// <summary>A native library, which carries no assembly version and is resolved lazily.</summary>
    Native,

    /// <summary>A satellite resource assembly, which lives under its culture's directory.</summary>
    Resource,
}

/// <summary>Whether a file the manifest declared is actually in the plugin directory.</summary>
public enum PluginFileAvailability
{
    /// <summary>The file was found, and therefore has a digest.</summary>
    Present,

    /// <summary>The file was declared and is not there, so there is nothing to hash.</summary>
    Absent,
}

/// <summary>Where a version on an inventory row came from.</summary>
/// <remarks>
/// Kept separate from the version itself so that nobody later reads a missing declaration as
/// <c>0.0.0.0</c> or drops a row for want of a version. A framework-dependent publish records no
/// version for the project's own entry and none at all for a native asset, and both of those are
/// ordinary rather than faults.
/// </remarks>
public enum PluginVersionSource
{
    /// <summary>The manifest declared it.</summary>
    DepsJsonDeclaration,

    /// <summary>The manifest declared none, so it was read from the assembly the process loaded.</summary>
    LoadedAssembly,

    /// <summary>Nothing declares one and nothing can supply one.</summary>
    NotDeclared,
}

/// <summary>Whether a declared file had been loaded when the inventory was materialized.</summary>
/// <remarks>
/// <see cref="Unknown"/> is not a failure to look. The runtime exposes no way to enumerate the native
/// libraries a load context has resolved, so a native row cannot be answered either way and reporting
/// <see cref="NotLoaded"/> for it would be a claim nobody checked.
/// </remarks>
public enum PluginFileLoadState
{
    /// <summary>This file's own bytes are loaded in the plugin's context.</summary>
    Loaded,

    /// <summary>This file is not loaded. For a managed assembly the host served instead, this is the
    /// truthful answer: the plugin's copy was never used.</summary>
    NotLoaded,

    /// <summary>Not answerable, which is the case for every native asset.</summary>
    Unknown,
}

/// <summary>
/// One file a plugin's dependency manifest declares, as it stands on disk.
/// </summary>
/// <remarks>
/// The inventory is built from the declaration rather than from the load context's assembly list,
/// because assemblies load lazily: an enumeration taken at any single moment omits every request-path
/// assembly and every native library, and an inventory that goes quiet exactly where the interesting
/// code lives is worse than one that admits its edges. The declaration is the complete set of files
/// the plugin shipped, so a responder can match a running process to a published artifact whether or
/// not a given assembly has been touched.
/// </remarks>
public sealed record PluginDeclaredFile
{
    private PluginDeclaredFile(
        string fileName,
        string declaredPath,
        string? resolvedRelativePath,
        PluginFileKind kind,
        Version? declaredAssemblyVersion,
        PluginVersionSource declaredVersionSource,
        PluginFileAvailability availability,
        string? sha256
    )
    {
        FileName = fileName;
        DeclaredPath = declaredPath;
        ResolvedRelativePath = resolvedRelativePath;
        Kind = kind;
        DeclaredAssemblyVersion = declaredAssemblyVersion;
        DeclaredVersionSource = declaredVersionSource;
        Availability = availability;
        Sha256 = sha256;
    }

    /// <summary>The file's name as it was published.</summary>
    public string FileName { get; }

    /// <summary>The path the manifest wrote, which is package-relative rather than published.</summary>
    public string DeclaredPath { get; }

    /// <summary>
    /// Where the file was found, relative to the plugin directory, or <see langword="null"/> when it
    /// was not found at all.
    /// </summary>
    public string? ResolvedRelativePath { get; }

    /// <summary>What kind of asset the manifest declared.</summary>
    public PluginFileKind Kind { get; }

    /// <summary>The version the manifest declared, or <see langword="null"/> when it declared none.</summary>
    public Version? DeclaredAssemblyVersion { get; }

    /// <summary>Whether a declared version exists at all.</summary>
    public PluginVersionSource DeclaredVersionSource { get; }

    /// <summary>Whether the declared file is in the plugin directory.</summary>
    public PluginFileAvailability Availability { get; }

    /// <summary>
    /// The SHA-256 of the file as it sits in the plugin directory, lower-case hexadecimal. Non-null if
    /// and only if <see cref="Availability"/> is <see cref="PluginFileAvailability.Present"/>: nothing
    /// hashes empty bytes, and no digest-of-nothing is ever presented as a file's digest.
    /// </summary>
    public string? Sha256 { get; }

    internal static PluginDeclaredFile Present(
        string fileName,
        string declaredPath,
        string resolvedRelativePath,
        PluginFileKind kind,
        Version? declaredAssemblyVersion,
        string sha256
    ) =>
        new(
            fileName,
            declaredPath,
            resolvedRelativePath,
            kind,
            declaredAssemblyVersion,
            declaredAssemblyVersion is null
                ? PluginVersionSource.NotDeclared
                : PluginVersionSource.DepsJsonDeclaration,
            PluginFileAvailability.Present,
            sha256
        );

    internal static PluginDeclaredFile Absent(
        string fileName,
        string declaredPath,
        PluginFileKind kind,
        Version? declaredAssemblyVersion
    ) =>
        new(
            fileName,
            declaredPath,
            resolvedRelativePath: null,
            kind,
            declaredAssemblyVersion,
            declaredAssemblyVersion is null
                ? PluginVersionSource.NotDeclared
                : PluginVersionSource.DepsJsonDeclaration,
            PluginFileAvailability.Absent,
            sha256: null
        );
}

/// <summary>
/// One inventory row, as it stands at the moment the caller asked for it.
/// </summary>
/// <param name="FileName">The file's name as it was published.</param>
/// <param name="DeclaredPath">The path the manifest wrote.</param>
/// <param name="ResolvedRelativePath">Where the file was found, or <see langword="null"/>.</param>
/// <param name="Kind">What kind of asset the manifest declared.</param>
/// <param name="DeclaredAssemblyVersion">
/// The version the manifest declared, which stays <see langword="null"/> when it declared none. It is
/// kept apart from <paramref name="EffectiveVersion"/> so that the absence of a declaration remains
/// visible rather than being filled in.
/// </param>
/// <param name="EffectiveVersion">The version the process actually has, when that is knowable.</param>
/// <param name="EffectiveVersionSource">Where <paramref name="EffectiveVersion"/> came from.</param>
/// <param name="Availability">Whether the declared file is in the plugin directory.</param>
/// <param name="Sha256">The digest of the file, non-null exactly when it is present.</param>
/// <param name="LoadState">
/// Whether this file's own bytes were loaded in the plugin's context when the row was produced. Read
/// at that moment rather than frozen when loading finished, because an assembly first touched inside a
/// contribution hook loads long after the loader's work is over.
/// </param>
public sealed record PluginInventoryRow(
    string FileName,
    string DeclaredPath,
    string? ResolvedRelativePath,
    PluginFileKind Kind,
    Version? DeclaredAssemblyVersion,
    Version? EffectiveVersion,
    PluginVersionSource EffectiveVersionSource,
    PluginFileAvailability Availability,
    string? Sha256,
    PluginFileLoadState LoadState
);

/// <summary>
/// An assembly the host served to a plugin in place of the plugin's own.
/// </summary>
/// <remarks>
/// Host-first substitution is silent and almost always correct, which is exactly why it needs a
/// record: it is the diagnostic for the one report that would otherwise arrive with no evidence behind
/// it, that a plugin works on its author's machine and not in the host.
/// </remarks>
/// <param name="AssemblyName">The simple name the plugin asked for.</param>
/// <param name="RequestedVersion">
/// The version the requesting assembly's reference carried, which is what the runtime asked for rather
/// than what the manifest declared. The two can differ, and neither stands in for the other.
/// </param>
/// <param name="HostVersion">The version the host served.</param>
/// <param name="DeclaredVersion">
/// The version the plugin's manifest declared for that assembly, or <see langword="null"/> when the
/// manifest declares none. A shared-framework assembly has no declaration, and inventing one would be
/// a claim the manifest never made.
/// </param>
public sealed record HostFirstSubstitution(
    string AssemblyName,
    Version? RequestedVersion,
    Version HostVersion,
    Version? DeclaredVersion
);
