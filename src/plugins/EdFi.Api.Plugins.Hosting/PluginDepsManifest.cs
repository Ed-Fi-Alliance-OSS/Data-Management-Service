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
/// One asset a plugin's dependency manifest declares, before anything has looked for it on disk.
/// </summary>
/// <param name="DeclaredPath">The path the manifest wrote, which is package-relative.</param>
/// <param name="Kind">Which section of the manifest declared it.</param>
/// <param name="DeclaredVersion">The declared assembly version, when the section carries one.</param>
/// <param name="Locale">
/// The culture a resource assembly belongs to, which is where a publish puts it and is therefore part
/// of finding it.
/// </param>
internal sealed record PluginDeclaredAsset(
    string DeclaredPath,
    PluginFileKind Kind,
    Version? DeclaredVersion,
    string? Locale
);

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
        IReadOnlyList<PluginDeclaredAssembly> declaredAssemblies,
        IReadOnlyList<PluginDeclaredAsset> declaredAssets
    )
    {
        DeclaresRuntimePack = declaresRuntimePack;
        DeclaredAssemblies = declaredAssemblies;
        DeclaredAssets = declaredAssets;
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

    /// <summary>
    /// Every asset the manifest declares, managed, native and resource alike, which is the set the
    /// inventory is built from.
    /// </summary>
    /// <remarks>
    /// A native asset carries no version and is still a file the plugin shipped, so it is listed rather
    /// than dropped for want of one. Dropping it would leave the inventory silent about exactly the
    /// files an incident responder is most likely to ask about.
    /// </remarks>
    internal IReadOnlyList<PluginDeclaredAsset> DeclaredAssets { get; }

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

            JsonElement selectedTarget = Require(
                target,
                JsonValueKind.Object,
                pluginName,
                manifestPath,
                "the selected target"
            );

            return new PluginDepsManifest(
                ReadDeclaresRuntimePack(root, pluginName, manifestPath),
                ReadDeclaredAssemblies(selectedTarget, pluginName, manifestPath),
                ReadDeclaredAssets(selectedTarget, pluginName, manifestPath)
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

                string simpleName = Path.GetFileNameWithoutExtension(asset.Name);

                // The simple name goes on to the skew preflight, which asks the runtime for the host's
                // copy by that name, and the runtime's own assembly-name parsing throws on an empty or
                // whitespace one before any binding is attempted. That throw is neither a
                // PluginLoadException nor the FileNotFoundException a missing assembly produces, so it
                // would leave the operator an unlabelled exception and a silent channel. Refused here
                // rather than skipped, for the same reason an unparseable version is: a declaration
                // dropped for want of a usable name is a declaration taken out of the preflight with
                // nobody told.
                if (string.IsNullOrWhiteSpace(simpleName))
                {
                    throw Unreadable(
                        pluginName,
                        manifestPath,
                        $"'{PluginDiagnosticText.Quote($"{library.Name}.runtime.{asset.Name}")}' declares "
                            + "a version for a path that names no assembly",
                        innerException: null
                    );
                }

                declared.Add(new PluginDeclaredAssembly(simpleName, version, library.Name));
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

    /// <summary>
    /// Reads every asset the selected target declares: the managed assemblies under <c>runtime</c>, the
    /// satellite assemblies under <c>resources</c>, the native libraries under <c>native</c>, and the
    /// RID-specific assets under <c>runtimeTargets</c>, whose kind comes from their own asset type.
    /// </summary>
    private static IReadOnlyList<PluginDeclaredAsset> ReadDeclaredAssets(
        JsonElement target,
        string pluginName,
        string manifestPath
    )
    {
        List<PluginDeclaredAsset> assets = [];

        foreach (JsonElement library in target.EnumerateObject().Select(entry => entry.Value))
        {
            ReadSection(library, "runtime", PluginFileKind.Managed);
            ReadSection(library, "native", PluginFileKind.Native);
            ReadSection(library, "resources", PluginFileKind.Resource);
            ReadRuntimeTargets(library);
        }

        return assets;

        void ReadSection(JsonElement libraryValue, string sectionName, PluginFileKind kind)
        {
            if (!libraryValue.TryGetProperty(sectionName, out JsonElement section))
            {
                return;
            }

            Require(section, JsonValueKind.Object, pluginName, manifestPath, sectionName);

            foreach (JsonProperty asset in section.EnumerateObject())
            {
                string location = $"{sectionName}.{asset.Name}";

                Require(asset.Value, JsonValueKind.Object, pluginName, manifestPath, location);

                assets.Add(
                    new PluginDeclaredAsset(
                        asset.Name,
                        kind,
                        ReadVersion(asset.Value, location),
                        ReadLocale(asset.Value, location)
                    )
                );
            }
        }

        void ReadRuntimeTargets(JsonElement libraryValue)
        {
            if (!libraryValue.TryGetProperty("runtimeTargets", out JsonElement runtimeTargets))
            {
                return;
            }

            Require(runtimeTargets, JsonValueKind.Object, pluginName, manifestPath, "runtimeTargets");

            foreach (JsonProperty asset in runtimeTargets.EnumerateObject())
            {
                string location = $"runtimeTargets.{asset.Name}";

                Require(asset.Value, JsonValueKind.Object, pluginName, manifestPath, location);

                // The asset type is what says whether a RID-specific asset is managed or native. A
                // native one carries no assemblyVersion, which is the known gap rather than a fault.
                PluginFileKind kind =
                    asset.Value.TryGetProperty("assetType", out JsonElement assetType)
                    && Require(
                            assetType,
                            JsonValueKind.String,
                            pluginName,
                            manifestPath,
                            $"{location}.assetType"
                        )
                        .ValueEquals("native")
                        ? PluginFileKind.Native
                        : PluginFileKind.Managed;

                assets.Add(
                    new PluginDeclaredAsset(
                        asset.Name,
                        kind,
                        ReadVersion(asset.Value, location),
                        ReadLocale(asset.Value, location)
                    )
                );
            }
        }

        Version? ReadVersion(JsonElement declaration, string location)
        {
            if (!declaration.TryGetProperty("assemblyVersion", out JsonElement declaredVersion))
            {
                return null;
            }

            Require(
                declaredVersion,
                JsonValueKind.String,
                pluginName,
                manifestPath,
                $"{location}.assemblyVersion"
            );

            if (!Version.TryParse(declaredVersion.GetString(), out Version? version))
            {
                throw Unreadable(
                    pluginName,
                    manifestPath,
                    $"'{PluginDiagnosticText.Quote(location)}.assemblyVersion' is "
                        + $"'{PluginDiagnosticText.Quote(declaredVersion.GetString())}', which is not an "
                        + "assembly version",
                    innerException: null
                );
            }

            return version;
        }

        string? ReadLocale(JsonElement declaration, string location)
        {
            if (!declaration.TryGetProperty("locale", out JsonElement locale))
            {
                return null;
            }

            return Require(locale, JsonValueKind.String, pluginName, manifestPath, $"{location}.locale")
                .GetString();
        }
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
