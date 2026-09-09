// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Turns what a manifest declares into what is actually in the plugin directory.
/// </summary>
/// <remarks>
/// <para>
/// A manifest names assets by their package-relative path, and a publish flattens them, so the
/// declared path and the published path are rarely the same string. Two candidates cover both shapes a
/// plugin can ship: a portable framework-dependent publish leaves <c>runtimes/&lt;rid&gt;/...</c> in
/// place and flattens the rest to the root, and a runtime-specific publish relocates the running RID's
/// assets to the root while the manifest goes on writing their package-relative paths. A resource
/// assembly gets a third candidate under its own culture directory, which is where a publish puts it.
/// </para>
/// <para>
/// Nothing here fabricates. A declared file that is not there is an absent row with no digest, because
/// a missing native asset is a lazy first-use failure by design rather than a load-time refusal, and
/// because a digest of nothing is not a digest. A file that is there and cannot be read is a refusal,
/// because an unreadable file is not an absent one and laundering the difference would hide a real
/// problem.
/// </para>
/// </remarks>
internal static class PluginFileInventory
{
    /// <summary>Builds the declared-file inventory for one plugin directory.</summary>
    /// <exception cref="PluginLoadException">A file that is present could not be read.</exception>
    internal static IReadOnlyList<PluginDeclaredFile> Build(
        string pluginName,
        string pluginDirectory,
        PluginDepsManifest manifest
    )
    {
        List<PluginDeclaredFile> files = [];

        // Two declarations of one published file are one row: a library's top-level runtime entry and
        // its runtimeTargets row for the running RID commonly resolve to the same bytes. Two
        // declarations that resolve nowhere stay separate, because nothing has shown them to be the
        // same file and merging them would drop a declaration the plugin made.
        HashSet<string> seenResolved = new(StringComparer.Ordinal);
        HashSet<string> seenDeclared = new(StringComparer.Ordinal);

        foreach (PluginDeclaredAsset asset in manifest.DeclaredAssets)
        {
            string fileName = Path.GetFileName(asset.DeclaredPath);

            if (fileName.Length == 0)
            {
                continue;
            }

            if (TryResolve(pluginDirectory, asset, fileName) is { } resolvedRelativePath)
            {
                if (!seenResolved.Add(resolvedRelativePath))
                {
                    continue;
                }

                files.Add(
                    PluginDeclaredFile.Present(
                        fileName,
                        asset.DeclaredPath,
                        resolvedRelativePath,
                        asset.Kind,
                        asset.DeclaredVersion,
                        ComputeSha256(
                            pluginName,
                            Path.Combine(pluginDirectory, resolvedRelativePath),
                            resolvedRelativePath
                        )
                    )
                );

                continue;
            }

            if (seenDeclared.Add(asset.DeclaredPath))
            {
                files.Add(
                    PluginDeclaredFile.Absent(fileName, asset.DeclaredPath, asset.Kind, asset.DeclaredVersion)
                );
            }
        }

        return files;
    }

    /// <summary>
    /// Finds where a declared asset actually sits, relative to the plugin directory.
    /// </summary>
    private static string? TryResolve(string pluginDirectory, PluginDeclaredAsset asset, string fileName)
    {
        foreach (string candidate in Candidates(asset, fileName))
        {
            string normalized = candidate.Replace('/', Path.DirectorySeparatorChar);

            if (File.Exists(Path.Combine(pluginDirectory, normalized)))
            {
                return normalized;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(PluginDeclaredAsset asset, string fileName)
    {
        // The path as the manifest wrote it, which is where a portable publish leaves a RID-specific
        // asset.
        yield return asset.DeclaredPath;

        // A satellite assembly lives under its culture, which is the one place its file name alone
        // would not find it and the one thing that keeps two cultures' copies apart.
        if (asset.Kind == PluginFileKind.Resource && !string.IsNullOrEmpty(asset.Locale))
        {
            yield return $"{asset.Locale}/{fileName}";
        }

        // Flattened to the plugin directory root, which is where a publish puts a top-level asset and
        // where a runtime-specific publish relocates the running RID's assets.
        yield return fileName;
    }

    private static string ComputeSha256(string pluginName, string path, string relativePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PluginLoadException(
                PluginLoadFailure.PluginFileUnreadable,
                pluginName,
                $"plugin '{PluginDiagnosticText.Quote(pluginName)}' declares "
                    + $"'{PluginDiagnosticText.Quote(relativePath)}', which is present and could not be "
                    + $"read: {PluginDiagnosticText.Quote(exception.Message)}. A file that cannot be read "
                    + "is not a file that is absent, and the inventory will not report it as one.",
                exception
            );
        }
    }
}
