// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Where the fixture plugin directories are staged, and what they are called.
/// </summary>
/// <remarks>
/// The fixture projects under Fixtures/ are published into the test output by
/// PluginFixtures.targets, one directory each. Tests address them through this type rather than
/// composing the path themselves, so that the staging layout is stated once.
/// </remarks>
internal static class PluginFixtures
{
    /// <summary>The staged plugin root, which is a real plugin root as far as the loader is concerned.</summary>
    internal static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>The well-formed plugin.</summary>
    internal const string Good = "Acme.Good";

    /// <summary>A plugin directory whose entry assembly exposes no plugin type.</summary>
    internal const string NoSubclass = "Acme.NoSubclass";

    /// <summary>Every fixture the staging target is expected to produce.</summary>
    internal static IReadOnlyList<string> All { get; } = [Good, NoSubclass];

    internal static string DirectoryOf(string name) => Path.Combine(Root, name);

    internal static string EntryAssemblyOf(string name) => Path.Combine(DirectoryOf(name), $"{name}.dll");

    internal static string ManifestOf(string name) => Path.Combine(DirectoryOf(name), $"{name}.deps.json");
}

/// <summary>
/// The staging target is infrastructure every later loader test rests on, so its output is asserted
/// rather than assumed. A fixture that failed to publish would otherwise surface as a loader test
/// failing for the wrong reason.
/// </summary>
[TestFixture]
public class Given_the_fixture_plugins_have_been_staged
{
    [Test]
    public void It_stages_them_under_a_single_plugin_root()
    {
        Directory.Exists(PluginFixtures.Root).Should().BeTrue();
    }

    [Test]
    public void It_stages_exactly_the_declared_fixtures_and_nothing_left_over()
    {
        // A fixture removed from the target's list leaves its published directory behind, where a
        // later test enumerating the root would still find it. This is what proves the staging prunes.
        Directory
            .GetDirectories(PluginFixtures.Root)
            .Select(Path.GetFileName)
            .Should()
            .BeEquivalentTo(PluginFixtures.All);
    }

    [TestCaseSource(typeof(PluginFixtures), nameof(PluginFixtures.All))]
    public void It_names_the_entry_assembly_for_the_directory(string name)
    {
        // The directory name, the entry assembly file name and the assembly's own name all have to be
        // one string; this is the first of those equalities and the one staging is responsible for.
        File.Exists(PluginFixtures.EntryAssemblyOf(name)).Should().BeTrue();
    }

    [TestCaseSource(typeof(PluginFixtures), nameof(PluginFixtures.All))]
    public void It_publishes_a_dependency_manifest_beside_the_entry_assembly(string name)
    {
        File.Exists(PluginFixtures.ManifestOf(name)).Should().BeTrue();
    }

    [TestCaseSource(typeof(PluginFixtures), nameof(PluginFixtures.All))]
    public void It_publishes_a_manifest_naming_the_target_framework(string name)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(PluginFixtures.ManifestOf(name)));

        // runtimeTarget names the framework the fixture was published for. It is deliberately not
        // asserted to discriminate a self-contained publish, because it does not: a RID-specific
        // framework-dependent publish names a runtime identifier here too, and a runtimepack entry
        // among the libraries is what actually distinguishes the two.
        manifest
            .RootElement.GetProperty("runtimeTarget")
            .GetProperty("name")
            .GetString()
            .Should()
            .StartWith(".NETCoreApp,Version=v10.0");
    }

    [TestCaseSource(typeof(PluginFixtures), nameof(PluginFixtures.All))]
    public void It_stages_every_managed_file_the_manifest_declares(string name)
    {
        // The entry assembly and the manifest are the staging target's declared outputs, so they are
        // the two files a timestamp check can see. This asserts the rest of the closure, which is what
        // a test over a half-staged fixture would otherwise fail on for the wrong reason.
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(PluginFixtures.ManifestOf(name)));

        string targetName = manifest
            .RootElement.GetProperty("runtimeTarget")
            .GetProperty("name")
            .GetString()!;
        JsonElement target = manifest.RootElement.GetProperty("targets").GetProperty(targetName);

        List<string> declared = [];

        foreach (JsonProperty library in target.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("runtime", out JsonElement runtime))
            {
                continue;
            }

            // A portable framework-dependent publish flattens every runtime asset to the plugin
            // directory root, so the declared path's file name is where the file actually sits. The
            // fuller mapping, which a RID-specific publish needs, belongs with the loader's inventory.
            declared.AddRange(runtime.EnumerateObject().Select(asset => Path.GetFileName(asset.Name)));
        }

        declared.Should().NotBeEmpty();
        declared
            .Where(file => !File.Exists(Path.Combine(PluginFixtures.DirectoryOf(name), file)))
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_carries_the_plugin_contract_into_the_plugin_directory()
    {
        // A published plugin brings its own copy of the contract, which is exactly the copy host-first
        // resolution has to decline to use. The assertions about which copy is served need that file
        // to be on disk, so its presence is pinned here rather than assumed there.
        File.Exists(Path.Combine(PluginFixtures.DirectoryOf(PluginFixtures.Good), "EdFi.Api.Plugins.dll"))
            .Should()
            .BeTrue();
    }
}
