// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

[TestFixture]
public class Given_a_dependency_manifest_that_is_not_json
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);

        File.WriteAllText(
            Path.Combine(_root.RootPath, PluginFixtures.Good, $"{PluginFixtures.Good}.deps.json"),
            "this file is not JSON at all"
        );

        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_with_a_named_failure_rather_than_a_raw_parse_error()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.DepsJsonUnreadable);
        _run.Failure!.InnerException.Should().NotBeNull();
    }
}

[TestFixture]
public class Given_a_dependency_manifest_naming_a_target_it_does_not_declare
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);

        // Valid JSON, and not the shape a publish produces. The target is selected by the name
        // runtimeTarget gives rather than by taking whatever single entry happens to be there, so a
        // manifest that names one it does not declare is refused rather than read as empty.
        File.WriteAllText(
            Path.Combine(_root.RootPath, PluginFixtures.Good, $"{PluginFixtures.Good}.deps.json"),
            """{"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/linux-x64"}, "targets": {".NETCoreApp,Version=v10.0": {}}}"""
        );

        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_rather_than_silently_reading_no_declarations()
    {
        // Reading the wrong target would make every skew declaration invisible, which is worse than
        // refusing: the preflight would pass on a plugin it had never actually examined.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.DepsJsonUnreadable);
    }
}

[TestFixture]
public class Given_a_dependency_manifest_carrying_a_byte_order_mark
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);

        string manifestPath = Path.Combine(
            _root.RootPath,
            PluginFixtures.Good,
            $"{PluginFixtures.Good}.deps.json"
        );

        // A publish writes no byte order mark, but an operator or a tool that rewrote the file may. The
        // bytes are still valid JSON, so refusing them would be a refusal over an encoding detail.
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath), new UTF8Encoding(true));

        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reads_the_manifest_anyway()
    {
        _run.Failure.Should().BeNull();
        _run.Result!.Plugins.Should().ContainSingle();
    }
}

[TestFixture]
public class Given_a_native_library_the_plugin_did_not_ship
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_defers_to_the_default_probing()
    {
        // The override answers with a handle, and returning zero is how it says "I have nothing; probe
        // as you normally would". Returning a handle it does not have, or throwing, would break every
        // native library a plugin does not itself ship.
        PluginLoadContext context = new(
            PluginFixtures.Good,
            Path.Combine(_root.RootPath, PluginFixtures.Good, $"{PluginFixtures.Good}.dll")
        );

        MethodInfo loadUnmanagedDll = typeof(PluginLoadContext).GetMethod(
            "LoadUnmanagedDll",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        object? handle = loadUnmanagedDll.Invoke(context, ["acme-no-such-native-library"]);

        handle.Should().Be(IntPtr.Zero);
    }
}
