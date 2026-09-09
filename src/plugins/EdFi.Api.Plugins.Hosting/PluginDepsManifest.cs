// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// A managed assembly a plugin's dependency manifest declares, with the version it declares for it.
/// </summary>
/// <param name="SimpleName">The assembly's simple name, taken from the declared file name.</param>
/// <param name="DeclaredVersion">The <c>assemblyVersion</c> the manifest records.</param>
/// <param name="Library">The manifest library the declaration belongs to, for diagnostics.</param>
internal sealed record PluginDeclaredAssembly(string SimpleName, Version DeclaredVersion, string Library);

/// <summary>
/// The parts of a plugin's <c>.deps.json</c> the loader reads before it constructs the plugin.
/// </summary>
/// <remarks>
/// No new manifest format is introduced anywhere in this design: the file <c>dotnet publish</c> already
/// emits is the dependency manifest. This type reads only what the load-time checks need. The fuller
/// file-by-file reading the inventory needs is a separate concern and is not done here.
/// </remarks>
internal sealed class PluginDepsManifest
{
    private PluginDepsManifest(
        bool declaresRuntimePack,
        IReadOnlyList<PluginDeclaredAssembly> declaredAssemblies
    )
    {
        DeclaresRuntimePack = declaresRuntimePack;
        DeclaredAssemblies = declaredAssemblies;
    }

    /// <summary>
    /// Whether the manifest declares a runtime pack, which is what a self-contained publish produces.
    /// </summary>
    /// <remarks>
    /// The discriminator is deliberately the library entry rather than a runtime identifier in
    /// <c>runtimeTarget</c>: a RID-specific framework-dependent publish names a runtime identifier
    /// there too, and that publish is legitimate. It is how a plugin ships native assets.
    /// </remarks>
    internal bool DeclaresRuntimePack { get; }

    /// <summary>
    /// Every managed assembly the manifest declares a version for, which is the set the skew preflight
    /// compares against the host.
    /// </summary>
    internal IReadOnlyList<PluginDeclaredAssembly> DeclaredAssemblies { get; }

    /// <summary>Reads the manifest at <paramref name="manifestPath"/>.</summary>
    /// <exception cref="PluginLoadException">
    /// The file is not readable as the shape a publish produces.
    /// </exception>
    internal static PluginDepsManifest Read(string pluginName, string manifestPath)
    {
        try
        {
            // Read as text rather than as bytes: File.ReadAllText detects and strips a byte order mark,
            // and JsonDocument.Parse over raw bytes does not, so a manifest carrying one would be
            // reported as unreadable when it is perfectly good JSON.
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;

            // The target is selected by name rather than by taking the only entry: a RID-specific
            // manifest can carry both a framework target and a framework/RID target, and reading the
            // wrong one would silently miss every declaration in the other.
            if (
                !root.TryGetProperty("runtimeTarget", out JsonElement runtimeTarget)
                || !runtimeTarget.TryGetProperty("name", out JsonElement targetNameElement)
                || targetNameElement.GetString() is not { } targetName
                || !root.TryGetProperty("targets", out JsonElement targets)
                || !targets.TryGetProperty(targetName, out JsonElement target)
            )
            {
                throw Unreadable(
                    pluginName,
                    manifestPath,
                    "it does not name a runtime target that its targets section declares",
                    innerException: null
                );
            }

            return new PluginDepsManifest(ReadDeclaresRuntimePack(root), ReadDeclaredAssemblies(target));
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Unreadable(pluginName, manifestPath, "it could not be read as JSON", exception);
        }
    }

    private static bool ReadDeclaresRuntimePack(JsonElement root)
    {
        if (!root.TryGetProperty("libraries", out JsonElement libraries))
        {
            return false;
        }

        return libraries
            .EnumerateObject()
            .Any(library =>
                library.Value.TryGetProperty("type", out JsonElement type)
                && string.Equals(type.GetString(), "runtimepack", StringComparison.Ordinal)
            );
    }

    private static IReadOnlyList<PluginDeclaredAssembly> ReadDeclaredAssemblies(JsonElement target)
    {
        List<PluginDeclaredAssembly> declared = [];

        foreach (JsonProperty library in target.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("runtime", out JsonElement runtime))
            {
                continue;
            }

            foreach (JsonProperty asset in runtime.EnumerateObject())
            {
                if (
                    asset.Value.TryGetProperty("assemblyVersion", out JsonElement declaredVersion)
                    && Version.TryParse(declaredVersion.GetString(), out Version? version)
                )
                {
                    declared.Add(
                        new PluginDeclaredAssembly(
                            Path.GetFileNameWithoutExtension(asset.Name),
                            version,
                            library.Name
                        )
                    );
                }
            }
        }

        return declared;
    }

    private static PluginLoadException Unreadable(
        string pluginName,
        string manifestPath,
        string reason,
        Exception? innerException
    ) =>
        new(
            PluginLoadFailure.DepsJsonUnreadable,
            pluginName,
            $"plugin '{PluginDiagnosticText.Quote(pluginName)}' has a dependency manifest at "
                + $"'{PluginDiagnosticText.Quote(manifestPath)}' that {reason}. It has to be the "
                + ".deps.json a dotnet publish of the plugin produced.",
            innerException
        );
}
