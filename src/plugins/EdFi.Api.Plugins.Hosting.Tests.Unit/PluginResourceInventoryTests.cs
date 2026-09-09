// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// A satellite resource assembly is the one asset kind whose published location its file name alone
/// cannot find, because a publish puts it under its culture's directory.
/// </summary>
[TestFixture]
public class Given_a_plugin_that_ships_a_satellite_resource_assembly
{
    private const string SatelliteFileName = "Acme.Localized.resources.dll";
    private const string PublishedPath = "fr/" + SatelliteFileName;

    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Localized);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    private LoadedPlugin Load()
    {
        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Localized);
        run.Failure.Should().BeNull();
        return run.Result!.Plugins[0];
    }

    [Test]
    public void It_lists_the_satellite_as_a_resource_with_a_digest()
    {
        PluginInventoryRow resource = Load()
            .MaterializeInventory()
            .Single(row => row.Kind == PluginFileKind.Resource);

        resource.FileName.Should().Be(SatelliteFileName);
        resource.Availability.Should().Be(PluginFileAvailability.Present);
        resource.Sha256.Should().NotBeNull();

        // A satellite carries no assembly version in the manifest, so the row says so rather than
        // inventing one, exactly as a native row does.
        resource.DeclaredAssemblyVersion.Should().BeNull();
        resource.EffectiveVersionSource.Should().Be(PluginVersionSource.NotDeclared);
    }

    [Test]
    public void It_is_a_real_satellite_the_plugin_can_read_from()
    {
        // Without this the row above could be describing a file that is a satellite in name only.
        LoadedPlugin plugin = Load();

        string greeting = (string)
            plugin
                .Instance.GetType()
                .Assembly.GetType("Acme.Localized.LocalizedPlugin")!
                .GetMethod("FrenchGreeting")!
                .Invoke(null, null)!;

        greeting.Should().Be("bonjour");
    }

    [Test]
    public void It_finds_a_satellite_declared_at_its_package_relative_path()
    {
        // The case the culture branch of the mapping exists for. A satellite from a project reference is
        // declared where the publish writes it, so the declared path alone finds it; one from a package
        // is declared under lib/<tfm>/<culture>/ while the publish writes it under the culture directory
        // only. This produces that second shape from a real publish.
        _root.SetResourceDeclaredPath(
            PluginFixtures.Localized,
            PublishedPath,
            $"lib/net10.0/{PublishedPath}"
        );

        // The file name alone is not at the plugin root, so the last-resort candidate cannot be what
        // finds it either. Only the culture directory can.
        File.Exists(Path.Combine(_root.RootPath, PluginFixtures.Localized, SatelliteFileName))
            .Should()
            .BeFalse();

        PluginInventoryRow resource = Load()
            .MaterializeInventory()
            .Single(row => row.Kind == PluginFileKind.Resource);

        resource.DeclaredPath.Should().Be($"lib/net10.0/{PublishedPath}");
        resource.Availability.Should().Be(PluginFileAvailability.Present);
        resource.ResolvedRelativePath.Should().Be(PublishedPath.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>
/// The control context in the native probe only means something if the probe's own directory cannot
/// satisfy the call by accident.
/// </summary>
[TestFixture]
public class Given_the_native_probe_host
{
    [Test]
    public void It_carries_no_native_library_of_its_own()
    {
        // If the probe shipped a copy of the library beside itself, the runtime's default probing would
        // find it and the no-override control would pass for a reason that has nothing to do with the
        // plugin under test.
        string probeDirectory = Path.GetDirectoryName(PluginFixtures.NativeProbeHost)!;

        Directory
            .EnumerateFiles(probeDirectory, "*e_sqlite3*", SearchOption.AllDirectories)
            .Should()
            .BeEmpty();
    }
}
