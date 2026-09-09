// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// A plugin root of this test's own, built from the staged fixtures.
/// </summary>
/// <remarks>
/// Tests do not point the loader at the staged tree directly. Each one assembles the root it needs, so
/// that a case can name a fixture something else, remove a file from it, or sit beside a symbolic link,
/// and so that one test's root cannot describe another test's expectations.
/// </remarks>
internal sealed class TemporaryPluginRoot : IDisposable
{
    private readonly string _base;

    private TemporaryPluginRoot(string basePath, string rootPath)
    {
        _base = basePath;
        RootPath = rootPath;
    }

    /// <summary>The plugin root to configure.</summary>
    internal string RootPath { get; }

    /// <summary>Creates an empty root inside a temporary tree of its own.</summary>
    internal static TemporaryPluginRoot Create(string rootName = "plugins")
    {
        string basePath = Path.Combine(Path.GetTempPath(), "edfi-plugin-tests", Guid.NewGuid().ToString("N"));
        string rootPath = Path.Combine(basePath, rootName);
        Directory.CreateDirectory(rootPath);

        return new TemporaryPluginRoot(basePath, rootPath);
    }

    /// <summary>A directory inside the temporary tree but outside the plugin root.</summary>
    internal string CreateDirectoryOutsideRoot(string name)
    {
        string path = Path.Combine(_base, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Copies a staged fixture into the root, optionally under another name.</summary>
    internal string Add(string fixtureName, string? asName = null) => AddTo(RootPath, fixtureName, asName);

    /// <summary>Copies a staged fixture into an arbitrary directory, optionally under another name.</summary>
    internal static string AddTo(string destinationRoot, string fixtureName, string? asName = null)
    {
        string stagedName = asName ?? fixtureName;
        string destination = Path.Combine(destinationRoot, stagedName);

        CopyDirectory(PluginFixtures.DirectoryOf(fixtureName), destination);

        if (!string.Equals(stagedName, fixtureName, StringComparison.Ordinal))
        {
            // The loader resolves <directory>/<directory>.dll, so a fixture staged under another name
            // has to carry files named for it. Renaming rather than rebuilding is the point of several
            // of these cases: the entry assembly's own name then disagrees with the directory.
            Rename(destination, $"{fixtureName}.dll", $"{stagedName}.dll");
            Rename(destination, $"{fixtureName}.deps.json", $"{stagedName}.deps.json");
        }

        return destination;
    }

    /// <summary>Removes a file from a plugin directory in the root.</summary>
    internal void Remove(string pluginName, string fileName)
    {
        File.Delete(Path.Combine(RootPath, pluginName, fileName));
    }

    /// <summary>The staged plugin's dependency manifest.</summary>
    internal string ManifestPathOf(string pluginName) =>
        Path.Combine(RootPath, pluginName, $"{pluginName}.deps.json");

    /// <summary>Replaces a staged plugin's dependency manifest with the given text.</summary>
    internal void WriteManifest(string pluginName, string json)
    {
        File.WriteAllText(ManifestPathOf(pluginName), json);
    }

    /// <summary>
    /// Rewrites the first declared <c>assemblyVersion</c> in a staged plugin's manifest, keeping every
    /// other byte of a real published manifest intact.
    /// </summary>
    /// <remarks>
    /// The point is to change exactly one value in a file a publish produced, so that a test about a
    /// malformed version is about that version rather than about a hand-written manifest that differs
    /// from a real one in ways nobody enumerated.
    /// </remarks>
    internal void ReplaceFirstDeclaredAssemblyVersion(string pluginName, JsonNode? value, bool remove = false)
    {
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPathOf(pluginName)))!;
        string targetName = manifest["runtimeTarget"]!["name"]!.GetValue<string>();

        foreach (KeyValuePair<string, JsonNode?> library in manifest["targets"]![targetName]!.AsObject())
        {
            if (library.Value?["runtime"] is not JsonObject runtime)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> asset in runtime)
            {
                if (asset.Value is JsonObject declaration && declaration.ContainsKey("assemblyVersion"))
                {
                    if (remove)
                    {
                        declaration.Remove("assemblyVersion");
                    }
                    else
                    {
                        declaration["assemblyVersion"] = value;
                    }

                    WriteManifest(pluginName, manifest.ToJsonString());
                    return;
                }
            }
        }

        throw new AssertionException(
            $"Fixture '{pluginName}' declares no assemblyVersion, so there is nothing to rewrite."
        );
    }

    /// <summary>
    /// Moves a declared managed dependency out of the top-level <c>runtime</c> section and into a
    /// <c>runtimeTargets</c> row, with the version given.
    /// </summary>
    /// <remarks>
    /// This is the shape a package with runtime-identifier-specific managed assets produces, and it is
    /// the one the skew preflight deliberately does not read. The record of what the host served is a
    /// different question from what to refuse, so a declaration made only here still has to reach it.
    /// </remarks>
    internal void MoveDeclarationToRuntimeTargets(string pluginName, string simpleName, string version)
    {
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPathOf(pluginName)))!;
        string targetName = manifest["runtimeTarget"]!["name"]!.GetValue<string>();

        foreach (KeyValuePair<string, JsonNode?> library in manifest["targets"]![targetName]!.AsObject())
        {
            if (library.Value is not JsonObject entry || entry["runtime"] is not JsonObject runtime)
            {
                continue;
            }

            string? declaredPath = runtime
                .Select(asset => asset.Key)
                .FirstOrDefault(path =>
                    Path.GetFileNameWithoutExtension(path).Equals(simpleName, StringComparison.Ordinal)
                );

            if (declaredPath is null)
            {
                continue;
            }

            runtime.Remove(declaredPath);

            if (runtime.Count == 0)
            {
                entry.Remove("runtime");
            }

            entry["runtimeTargets"] = new JsonObject
            {
                [declaredPath] = new JsonObject
                {
                    ["rid"] = "any",
                    ["assetType"] = "runtime",
                    ["assemblyVersion"] = version,
                    ["fileVersion"] = version,
                },
            };

            WriteManifest(pluginName, manifest.ToJsonString());
            return;
        }

        throw new AssertionException(
            $"Fixture '{pluginName}' declares no runtime asset named '{simpleName}'."
        );
    }

    /// <summary>Sets the declared version of one managed dependency, wherever it is declared.</summary>
    internal void SetDeclaredVersion(string pluginName, string simpleName, string version)
    {
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPathOf(pluginName)))!;
        string targetName = manifest["runtimeTarget"]!["name"]!.GetValue<string>();

        foreach (KeyValuePair<string, JsonNode?> library in manifest["targets"]![targetName]!.AsObject())
        {
            if (library.Value?["runtime"] is not JsonObject runtime)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> asset in runtime)
            {
                if (
                    Path.GetFileNameWithoutExtension(asset.Key).Equals(simpleName, StringComparison.Ordinal)
                    && asset.Value is JsonObject declaration
                    && declaration.ContainsKey("assemblyVersion")
                )
                {
                    declaration["assemblyVersion"] = version;
                    WriteManifest(pluginName, manifest.ToJsonString());
                    return;
                }
            }
        }

        throw new AssertionException($"Fixture '{pluginName}' declares no version for '{simpleName}'.");
    }

    /// <summary>
    /// Lowers every top-level declared <c>assemblyVersion</c> that is higher than the copy this host
    /// carries, so a fixture built over a real package can be loaded in this process, and reports what
    /// it changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because of a measurement, not a convenience. A fixture referencing
    /// Microsoft.Data.SqlClient declares that package's closure, and the preflight correctly refuses
    /// it here: measured, the manifest declares <c>System.Configuration.ConfigurationManager</c>
    /// 9.0.0.0 while this test process carries 4.0.0.0, which arrives with the test platform. That is
    /// the designed refusal working on a genuine package rather than a defect, and a real host that
    /// serves SqlClient's closure would not hit it.
    /// </para>
    /// <para>
    /// A fixture whose subject is the <em>shape</em> of a manifest therefore reads the staged manifest
    /// untouched, and only a case that has to get the plugin loaded applies this. Nothing here invents
    /// a version: each rewritten value is the version this host actually carries, and the returned list
    /// names every change so a test can report exactly what it stood on.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<string> LowerDeclarationsAboveHostVersions(string pluginName)
    {
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPathOf(pluginName)))!;
        string targetName = manifest["runtimeTarget"]!["name"]!.GetValue<string>();
        List<string> lowered = [];

        foreach (KeyValuePair<string, JsonNode?> library in manifest["targets"]![targetName]!.AsObject())
        {
            if (library.Value?["runtime"] is not JsonObject runtime)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> asset in runtime)
            {
                if (
                    asset.Value is not JsonObject declaration
                    || declaration["assemblyVersion"]?.GetValue<string>() is not { } declaredText
                    || !Version.TryParse(declaredText, out Version? declared)
                )
                {
                    continue;
                }

                string simpleName = Path.GetFileNameWithoutExtension(asset.Key);

                if (
                    !HostAssemblies.TryGetVersion(simpleName, out Version hostVersion)
                    || hostVersion >= declared
                )
                {
                    continue;
                }

                declaration["assemblyVersion"] = hostVersion.ToString();
                lowered.Add($"{simpleName} {declared} -> {hostVersion}");
            }
        }

        WriteManifest(pluginName, manifest.ToJsonString());

        return lowered;
    }

    /// <summary>
    /// Rewrites the declared path of a resource asset, keeping the file where the publish put it.
    /// </summary>
    /// <remarks>
    /// A satellite that comes from a project reference is declared at the same path the publish writes,
    /// so it is found by the declared path alone. One that comes from a package is declared at its
    /// package-relative path, <c>lib/&lt;tfm&gt;/&lt;culture&gt;/...</c>, while the publish writes it
    /// under the culture directory only. This produces that second shape from a real publish, which is
    /// the only thing that exercises the culture branch of the mapping.
    /// </remarks>
    internal void SetResourceDeclaredPath(string pluginName, string fromPath, string toPath)
    {
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPathOf(pluginName)))!;
        string targetName = manifest["runtimeTarget"]!["name"]!.GetValue<string>();

        foreach (KeyValuePair<string, JsonNode?> library in manifest["targets"]![targetName]!.AsObject())
        {
            if (library.Value is not JsonObject entry || entry["resources"] is not JsonObject resources)
            {
                continue;
            }

            if (resources[fromPath] is not JsonNode declaration)
            {
                continue;
            }

            resources.Remove(fromPath);
            resources[toPath] = declaration.DeepClone();
            WriteManifest(pluginName, manifest.ToJsonString());
            return;
        }

        throw new AssertionException($"Fixture '{pluginName}' declares no resource asset at '{fromPath}'.");
    }

    /// <summary>Writes bytes that are not a managed assembly where an entry assembly belongs.</summary>
    internal string AddCorruptPlugin(string pluginName)
    {
        string directory = Path.Combine(RootPath, pluginName);
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, $"{pluginName}.dll"),
            "This is not a managed assembly. It is deliberately not a PE file at all.",
            Encoding.UTF8
        );

        // A manifest of the shape a publish produces, so the run reaches the entry assembly rather than
        // stopping at a missing manifest. It is written here rather than committed, because a corrupt
        // binary does not belong in source control.
        File.WriteAllText(
            Path.Combine(directory, $"{pluginName}.deps.json"),
            JsonSerializer.Serialize(
                new
                {
                    runtimeTarget = new { name = ".NETCoreApp,Version=v10.0", signature = "" },
                    compilationOptions = new { },
                    targets = new Dictionary<string, object>
                    {
                        [".NETCoreApp,Version=v10.0"] = new Dictionary<string, object>
                        {
                            [$"{pluginName}/1.0.0"] = new
                            {
                                runtime = new Dictionary<string, object> { [$"{pluginName}.dll"] = new { } },
                            },
                        },
                    },
                    libraries = new Dictionary<string, object>
                    {
                        [$"{pluginName}/1.0.0"] = new { type = "project", serviceable = false },
                    },
                },
                new JsonSerializerOptions { WriteIndented = true }
            ),
            Encoding.UTF8
        );

        return directory;
    }

    /// <summary>
    /// Creates a directory symbolic link, or ends the test as inconclusive when the platform refuses.
    /// </summary>
    /// <remarks>
    /// Windows requires elevation or developer mode to create one. Ending the test as inconclusive with
    /// a message that says so keeps a run on such a machine visibly incomplete rather than falsely
    /// green; the case is covered for real on Linux.
    /// </remarks>
    internal static string CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return linkPath;
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                "This platform refused to create a directory symbolic link "
                    + $"({exception.GetType().Name}: {exception.Message}). The containment checks that "
                    + "need one are covered on a platform that permits it; this run does not cover them."
            );

            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A loaded plugin assembly is held by a non-collectible context for the life of the
            // process, so its file can still be locked. Leaving a temporary directory behind is not
            // worth failing a passing test over.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void Rename(string directory, string from, string to)
    {
        string source = Path.Combine(directory, from);

        if (File.Exists(source))
        {
            File.Move(source, Path.Combine(directory, to));
        }
    }
}

/// <summary>What one loader run produced, including everything it wrote to its diagnostic channel.</summary>
internal sealed record PluginLoaderRun(
    LoadedPlugins? Result,
    PluginLoadException? Failure,
    string Diagnostics
)
{
    internal IReadOnlyList<string> DiagnosticLines =>
        Diagnostics.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Runs the real loader against a root, capturing its channel rather than redirecting the process's own.
/// </summary>
internal static class PluginLoaderProbe
{
    /// <summary>The contract set a host normally passes: assembly names, never package ids.</summary>
    internal static readonly string[] Contracts = ["EdFi.Api.Plugins"];

    internal static PluginLoaderRun Run(
        string root,
        string allowed,
        IReadOnlyCollection<string>? contracts = null
    )
    {
        StringWriter diagnostics = new();

        try
        {
            LoadedPlugins result = PluginLoader.Load(
                Configuration(root, allowed),
                contracts ?? Contracts,
                diagnostics
            );

            return new PluginLoaderRun(result, null, diagnostics.ToString());
        }
        catch (PluginLoadException exception)
        {
            return new PluginLoaderRun(null, exception, diagnostics.ToString());
        }
    }

    /// <summary>Runs and asserts the load failed, returning the run so the reason can be inspected.</summary>
    internal static PluginLoaderRun RunExpectingFailure(
        string root,
        string allowed,
        IReadOnlyCollection<string>? contracts = null
    )
    {
        PluginLoaderRun run = Run(root, allowed, contracts);

        if (run.Failure is null)
        {
            throw new AssertionException(
                "Expected a PluginLoadException, but the load succeeded with "
                    + $"[{string.Join(", ", run.Result!.Plugins.Select(plugin => plugin.Name))}]. "
                    + $"Channel: {run.Diagnostics}"
            );
        }

        return run;
    }

    private static IConfiguration Configuration(string root, string allowed) =>
        new ConfigurationBuilder()
            .AddJsonStream(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        "{\"Plugins\": {\"Directory\": "
                            + JsonSerializer.Serialize(root)
                            + ", \"Allowed\": "
                            + JsonSerializer.Serialize(allowed)
                            + "}}"
                    )
                )
            )
            .Build();
}
