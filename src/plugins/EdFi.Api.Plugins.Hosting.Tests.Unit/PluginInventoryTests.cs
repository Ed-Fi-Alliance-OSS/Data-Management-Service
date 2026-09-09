// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Digests the test computes for itself, by a different route from the one the loader takes, so that a
/// row's digest is checked rather than compared against the same code that produced it.
/// </summary>
internal static class IndependentDigest
{
    internal static string Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

/// <summary>
/// Finds the native rows of an inventory, and among them the one the runtime can actually load.
/// </summary>
/// <remarks>
/// A plugin can ship more than one native file for one library, and the measured case is exactly that:
/// the SQLite package publishes a shared library and a static archive side by side for its Linux
/// runtime identifiers, and only a shared library for Windows. Both are files the plugin shipped, so
/// both are inventory rows; only one of them is the thing a P/Invoke resolves.
/// </remarks>
internal static class NativeRows
{
    internal static string SharedLibraryExtension
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return ".dll";
            }

            return OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        }
    }

    internal static IEnumerable<PluginInventoryRow> Of(IEnumerable<PluginInventoryRow> inventory) =>
        inventory.Where(row => row.Kind == PluginFileKind.Native);

    internal static PluginInventoryRow SharedLibrary(IEnumerable<PluginInventoryRow> inventory) =>
        Of(inventory).Single(row => row.FileName.EndsWith(SharedLibraryExtension, StringComparison.Ordinal));
}

[TestFixture]
public class Given_a_plugin_that_ships_a_private_dependency_inventory
{
    private TemporaryPluginRoot _root = null!;
    private LoadedPlugin _plugin = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.PrivateDependency);

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.PrivateDependency);
        run.Failure.Should().BeNull();
        _plugin = run.Result!.Plugins[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_lists_the_entry_assembly_and_the_private_dependency()
    {
        // Built from the declaration rather than from the context's assembly list, so a dependency
        // nothing has touched is in the inventory rather than missing from it.
        _plugin
            .MaterializeInventory()
            .Select(row => row.FileName)
            .Should()
            .Contain([$"{PluginFixtures.PrivateDependency}.dll", "Acme.Private.dll"]);
    }

    [Test]
    public void It_reports_the_entry_assembly_version_as_coming_from_the_assembly()
    {
        // A framework-dependent publish writes no version for the project's own entry, so the row has no
        // declared version and takes its effective one from the assembly the process loaded. Reporting
        // that null as 0.0.0.0, or dropping the row for want of a version, are the two things this keeps
        // anybody from doing later.
        PluginInventoryRow entry = _plugin
            .MaterializeInventory()
            .Single(row => row.FileName == $"{PluginFixtures.PrivateDependency}.dll");

        entry.DeclaredAssemblyVersion.Should().BeNull();
        entry.EffectiveVersion.Should().Be(_plugin.EntryAssemblyVersion);
        entry.EffectiveVersionSource.Should().Be(PluginVersionSource.LoadedAssembly);
    }

    [Test]
    public void It_hashes_the_file_that_is_actually_in_the_plugin_directory()
    {
        PluginInventoryRow dependency = _plugin
            .MaterializeInventory()
            .Single(row => row.FileName == "Acme.Private.dll");

        dependency.Availability.Should().Be(PluginFileAvailability.Present);
        dependency
            .Sha256.Should()
            .Be(IndependentDigest.Of(Path.Combine(_plugin.Directory, dependency.ResolvedRelativePath!)));
        dependency.DeclaredAssemblyVersion.Should().Be(new Version(1, 0, 0, 0));
        dependency.EffectiveVersionSource.Should().Be(PluginVersionSource.DepsJsonDeclaration);
    }

    [Test]
    public void It_reads_the_load_state_when_the_inventory_is_materialized_rather_than_when_loading_finished()
    {
        // The assertion that proves the read is late-bound. A flag captured when loading finished would
        // report this dependency as unloaded for the life of the process, including while the process
        // was running it.
        _plugin
            .MaterializeInventory()
            .Single(row => row.FileName == "Acme.Private.dll")
            .LoadState.Should()
            .Be(PluginFileLoadState.NotLoaded);

        Type pluginType = _plugin.Instance.GetType();
        pluginType.GetMethod("DescribePrivateDependency")!.Invoke(null, null);

        _plugin
            .MaterializeInventory()
            .Single(row => row.FileName == "Acme.Private.dll")
            .LoadState.Should()
            .Be(PluginFileLoadState.Loaded);
    }
}

[TestFixture]
public class Given_a_plugin_that_ships_a_native_library
{
    private TemporaryPluginRoot _root = null!;
    private LoadedPlugin _plugin = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Native);

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Native);
        run.Failure.Should().BeNull();
        _plugin = run.Result!.Plugins[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_resolves_the_native_library_and_calls_into_it()
    {
        // Nothing in the managed Load override reaches a native library, so this only works because the
        // context answers for unmanaged dependencies too. The value is SQLite's own version number,
        // which is a real answer from real native code rather than a resolution that returned a path.
        int version = (int)
            _plugin
                .Instance.GetType()
                .Assembly.GetType("Acme.Native.NativePlugin")!
                .GetMethod("NativeLibraryVersion")!
                .Invoke(null, null)!;

        version.Should().BeGreaterThan(3_000_000);
    }

    [Test]
    public void It_lists_the_native_library_with_a_digest_and_no_version()
    {
        IReadOnlyList<PluginInventoryRow> inventory = _plugin.MaterializeInventory();
        PluginInventoryRow native = NativeRows.SharedLibrary(inventory);

        native.FileName.Should().Contain("e_sqlite3");
        native.Availability.Should().Be(PluginFileAvailability.Present);
        native
            .Sha256.Should()
            .Be(IndependentDigest.Of(Path.Combine(_plugin.Directory, native.ResolvedRelativePath!)));

        // A native asset carries no assembly version anywhere, and saying so is the point: the row is
        // listed with an explicit no-declaration source rather than dropped or filled in. This holds for
        // every native file the plugin shipped, not only the one a P/Invoke resolves.
        NativeRows
            .Of(inventory)
            .Should()
            .OnlyContain(row =>
                row.DeclaredAssemblyVersion == null
                && row.EffectiveVersion == null
                && row.EffectiveVersionSource == PluginVersionSource.NotDeclared
            );
    }

    [Test]
    public void It_reports_the_native_load_state_as_unknown_rather_than_guessing()
    {
        // The runtime exposes no way to enumerate the native libraries a context has resolved. Reporting
        // this one as not loaded would be a claim nobody checked, and it would be wrong here: the test
        // above called into it.
        NativeRows
            .Of(_plugin.MaterializeInventory())
            .Should()
            .OnlyContain(row => row.LoadState == PluginFileLoadState.Unknown);
    }

    [Test]
    public void It_finds_the_native_library_where_the_publish_put_it_rather_than_where_the_manifest_says()
    {
        // The manifest declares it under runtimes/<rid>/native/ and a runtime-specific publish relocates
        // it to the plugin directory root. Both paths are recorded, which is what lets a responder match
        // a row to the package it came from as well as to the file on disk.
        PluginInventoryRow native = NativeRows.SharedLibrary(_plugin.MaterializeInventory());

        native.DeclaredPath.Should().Contain("runtimes/");
        native.ResolvedRelativePath.Should().NotContain("runtimes");
    }
}

[TestFixture]
public class Given_a_plugin_whose_native_library_is_missing
{
    private TemporaryPluginRoot _root = null!;
    private LoadedPlugin _plugin = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Native);

        // The design records a missing native asset as a lazy first-use failure rather than a load-time
        // check, because .deps.json describes native assets per runtime identifier and the runtime
        // resolves them lazily. This removes the file the publish actually placed.
        foreach (
            string nativeFile in Directory.EnumerateFiles(
                Path.Combine(_root.RootPath, PluginFixtures.Native),
                "*e_sqlite3*",
                SearchOption.AllDirectories
            )
        )
        {
            File.Delete(nativeFile);
        }

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Native);
        run.Failure.Should().BeNull();
        _plugin = run.Result!.Plugins[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_still_loads_because_native_resolution_is_lazy()
    {
        _plugin.Name.Should().Be(PluginFixtures.Native);
    }

    [Test]
    public void It_lists_the_declaration_with_no_digest_rather_than_dropping_it()
    {
        // A declared file that is not there is still something the plugin said it shipped, and an
        // inventory that quietly omitted it would be silent exactly where a responder is looking.
        // Nothing hashes empty bytes to fill the column.
        IEnumerable<PluginInventoryRow> native = NativeRows.Of(_plugin.MaterializeInventory());

        native.Should().NotBeEmpty();
        native
            .Should()
            .OnlyContain(row =>
                row.Availability == PluginFileAvailability.Absent
                && row.Sha256 == null
                && row.ResolvedRelativePath == null
                && row.DeclaredPath.Contains("e_sqlite3")
            );
    }

    [Test]
    public void It_offers_the_runtime_nothing_and_defers_to_the_default_probing()
    {
        // The loader's half of "not detected at load": the plugin shipped no such file, so the context
        // has no path to give and returns zero, which is how the override says to probe as usual. What
        // happens next is the runtime's business, and by design it is a DllNotFoundException at first
        // use rather than a refusal here.
        //
        // That last step is deliberately not asserted in this process. Windows and Linux both resolve a
        // native module by name once it is loaded, so a sibling fixture that has already loaded a module
        // of this name satisfies the call regardless of what this plugin ships, and an assertion here
        // would pass or fail on test order rather than on behaviour. The assertion that does hold
        // everywhere is the one below.
        PluginLoadContext context = new(
            PluginFixtures.Native,
            Path.Combine(_plugin.Directory, $"{PluginFixtures.Native}.dll")
        );

        MethodInfo loadUnmanagedDll = typeof(PluginLoadContext).GetMethod(
            "LoadUnmanagedDll",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        loadUnmanagedDll.Invoke(context, ["e_sqlite3"]).Should().Be(IntPtr.Zero);
    }
}

[TestFixture]
public class Given_a_plugin_shipping_an_older_copy_of_an_assembly_the_host_carries
{
    private TemporaryPluginRoot _root = null!;
    private LoadedPlugin _plugin = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Substitution);

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Substitution);
        run.Failure.Should().BeNull();
        _plugin = run.Result!.Plugins[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_records_nothing_until_the_assembly_is_actually_resolved()
    {
        // The record is of resolutions that happened. A preflight knows this plugin declares an older
        // copy and could predict a substitution from that, but predicting one for a dependency nothing
        // has asked for would report a substitution that has not occurred.
        _plugin.MaterializeSubstitutions().Should().BeEmpty();
    }

    [Test]
    public void It_records_the_substitution_once_the_assembly_is_resolved()
    {
        string described = (string)
            _plugin
                .Instance.GetType()
                .Assembly.GetType("Acme.Substitution.SubstitutionPlugin")!
                .GetMethod("DescribeHostShared")!
                .Invoke(null, null)!;

        // The host's copy answered, not the plugin's, which is the substitution itself.
        described.Should().Be("Acme.HostShared 1.0.0");

        HostFirstSubstitution substitution = _plugin.MaterializeSubstitutions().Single();

        substitution.AssemblyName.Should().Be("Acme.HostShared");
        substitution.HostVersion.Should().Be(new Version(1, 0, 0, 0));

        // The manifest declaration is the comparison that matters, because it is what the plugin
        // shipped. The reference version is a separate fact and is carried separately rather than
        // standing in for it.
        substitution.DeclaredVersion.Should().Be(new Version(0, 5, 0, 0));
        substitution.RequestedVersion.Should().Be(new Version(0, 5, 0, 0));
    }

    [Test]
    public void It_reports_the_plugins_own_copy_as_not_loaded()
    {
        _plugin
            .Instance.GetType()
            .Assembly.GetType("Acme.Substitution.SubstitutionPlugin")!
            .GetMethod("DescribeHostShared")!
            .Invoke(null, null);

        // The plugin's own file is on disk and was never used, so the truthful load state is that it is
        // not loaded. Where it went instead is what the substitution record is for.
        _plugin
            .MaterializeInventory()
            .Single(row => row.FileName == "Acme.HostShared.dll")
            .LoadState.Should()
            .Be(PluginFileLoadState.NotLoaded);
    }
}

[TestFixture]
public class Given_a_well_formed_plugin_whose_declarations_all_agree_with_the_host
{
    private TemporaryPluginRoot _root = null!;
    private LoadedPlugin _plugin = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Good);
        run.Failure.Should().BeNull();
        _plugin = run.Result!.Plugins[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_records_no_substitution_when_the_host_serves_the_same_version()
    {
        // Host-first serves this plugin the host's contract copy, and the versions agree, so nothing was
        // substituted in any sense worth reporting. A record here would be noise in the one event an
        // operator reads to find real ones.
        _plugin.MaterializeSubstitutions().Should().BeEmpty();
    }

    [Test]
    public void It_gives_every_present_file_a_digest_and_no_absent_file_one()
    {
        IReadOnlyList<PluginInventoryRow> inventory = _plugin.MaterializeInventory();

        // Stated as one invariant rather than two filtered assertions, so that it still says something
        // when a fixture happens to have no absent files at all.
        inventory.Should().NotBeEmpty();
        inventory
            .Should()
            .OnlyContain(row =>
                (row.Availability == PluginFileAvailability.Present)
                == (row.Sha256 != null && row.Sha256.Length == 64)
            );
    }
}
