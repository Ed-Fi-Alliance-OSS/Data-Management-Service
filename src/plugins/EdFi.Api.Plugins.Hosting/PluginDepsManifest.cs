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
/// <para>
/// No new manifest format is introduced anywhere in this design: the file <c>dotnet publish</c>
/// already emits is the dependency manifest. This type reads only what the load-time checks need. The
/// fuller file-by-file reading the inventory needs is a separate concern and is not done here.
/// </para>
/// <para>
/// Every read checks the JSON kind before it reads a value. A manifest is a file a third party
/// produced and an operator dropped into a directory, so anything about its shape can be wrong, and
/// <see cref="JsonElement"/>'s accessors throw <see cref="InvalidOperationException"/> rather than
/// returning null when the kind is not what was asked for. A manifest that is a JSON array, or whose
/// <c>runtimeTarget</c> is null, is an ordinary malformed plugin rather than an unforeseen host
/// failure, and it has to arrive as the named refusal this loader promises.
/// </para>
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

            JsonElement root = Require(
                document.RootElement,
                JsonValueKind.Object,
                pluginName,
                manifestPath,
                "the document"
            );

            // The target is selected by name rather than by taking the only entry: a RID-specific
            // manifest can carry both a framework target and a framework/RID target, and reading the
            // wrong one would silently miss every declaration in the other.
            JsonElement runtimeTarget = Require(
                root,
                "runtimeTarget",
                JsonValueKind.Object,
                pluginName,
                manifestPath
            );
            JsonElement targetName = Require(
                runtimeTarget,
                "name",
                JsonValueKind.String,
                pluginName,
                manifestPath,
                "runtimeTarget."
            );
            JsonElement targets = Require(root, "targets", JsonValueKind.Object, pluginName, manifestPath);

            if (!targets.TryGetProperty(targetName.GetString()!, out JsonElement target))
            {
                throw Unreadable(
                    pluginName,
                    manifestPath,
                    $"its targets section declares no '{PluginDiagnosticText.Quote(targetName.GetString()!)}', "
                        + "which is the runtime target it names",
                    innerException: null
                );
            }

            return new PluginDepsManifest(
                ReadDeclaresRuntimePack(root, pluginName, manifestPath),
                ReadDeclaredAssemblies(
                    Require(target, JsonValueKind.Object, pluginName, manifestPath, "the selected target"),
                    pluginName,
                    manifestPath
                )
            );
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            throw Unreadable(pluginName, manifestPath, "it could not be read as JSON", exception);
        }
    }

    private static bool ReadDeclaresRuntimePack(JsonElement root, string pluginName, string manifestPath)
    {
        // An absent libraries section is not malformed: it simply declares no runtime pack.
        if (!root.TryGetProperty("libraries", out JsonElement libraries))
        {
            return false;
        }

        Require(libraries, JsonValueKind.Object, pluginName, manifestPath, "libraries");

        foreach (JsonProperty library in libraries.EnumerateObject())
        {
            Require(
                library.Value,
                JsonValueKind.Object,
                pluginName,
                manifestPath,
                $"libraries.{library.Name}"
            );

            if (
                library.Value.TryGetProperty("type", out JsonElement type)
                && Require(
                        type,
                        JsonValueKind.String,
                        pluginName,
                        manifestPath,
                        $"libraries.{library.Name}.type"
                    )
                    .ValueEquals("runtimepack")
            )
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<PluginDeclaredAssembly> ReadDeclaredAssemblies(
        JsonElement target,
        string pluginName,
        string manifestPath
    )
    {
        List<PluginDeclaredAssembly> declared = [];

        foreach (JsonProperty library in target.EnumerateObject())
        {
            Require(library.Value, JsonValueKind.Object, pluginName, manifestPath, library.Name);

            // A library with no runtime assets declares nothing to compare, which is ordinary.
            if (!library.Value.TryGetProperty("runtime", out JsonElement runtime))
            {
                continue;
            }

            Require(runtime, JsonValueKind.Object, pluginName, manifestPath, $"{library.Name}.runtime");

            foreach (JsonProperty asset in runtime.EnumerateObject())
            {
                Require(
                    asset.Value,
                    JsonValueKind.Object,
                    pluginName,
                    manifestPath,
                    $"{library.Name}.runtime.{asset.Name}"
                );

                // An absent assemblyVersion is legitimate and common: a framework-dependent publish
                // writes none for the project's own entry. A present one that is not a version is not
                // legitimate, and dropping it silently would remove an assembly from the skew preflight
                // without anybody being told, which is the one outcome this check exists to prevent.
                if (!asset.Value.TryGetProperty("assemblyVersion", out JsonElement declaredVersion))
                {
                    continue;
                }

                string location = $"{library.Name}.runtime.{asset.Name}.assemblyVersion";

                Require(declaredVersion, JsonValueKind.String, pluginName, manifestPath, location);

                if (!Version.TryParse(declaredVersion.GetString(), out Version? version))
                {
                    throw Unreadable(
                        pluginName,
                        manifestPath,
                        $"'{PluginDiagnosticText.Quote(location)}' is "
                            + $"'{PluginDiagnosticText.Quote(declaredVersion.GetString())}', which is not "
                            + "an assembly version",
                        innerException: null
                    );
                }

                declared.Add(
                    new PluginDeclaredAssembly(
                        Path.GetFileNameWithoutExtension(asset.Name),
                        version,
                        library.Name
                    )
                );
            }
        }

        return declared;
    }

    /// <summary>Requires a property of the given kind, naming where it was expected when it is not.</summary>
    private static JsonElement Require(
        JsonElement parent,
        string propertyName,
        JsonValueKind kind,
        string pluginName,
        string manifestPath,
        string locationPrefix = ""
    )
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement property))
        {
            throw Unreadable(
                pluginName,
                manifestPath,
                $"it declares no '{locationPrefix}{propertyName}'",
                innerException: null
            );
        }

        return Require(property, kind, pluginName, manifestPath, $"{locationPrefix}{propertyName}");
    }

    /// <summary>Requires a value of the given kind, naming where it was expected when it is not.</summary>
    private static JsonElement Require(
        JsonElement element,
        JsonValueKind kind,
        string pluginName,
        string manifestPath,
        string location
    )
    {
        if (element.ValueKind != kind)
        {
            throw Unreadable(
                pluginName,
                manifestPath,
                $"'{PluginDiagnosticText.Quote(location)}' is {element.ValueKind} where "
                    + $"{kind} was expected",
                innerException: null
            );
        }

        return element;
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
                + $"'{PluginDiagnosticText.Quote(manifestPath)}' that is not the shape a publish "
                + $"produces: {reason}. It has to be the .deps.json a dotnet publish of the plugin "
                + "produced.",
            innerException
        );
}
