// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Runs the native probe in a process that has loaded no native module before it starts.
/// </summary>
/// <remarks>
/// Both platforms resolve a native module by name once it is loaded, so any assertion about a native
/// library failing to resolve is meaningless in a process where a sibling test has already loaded a
/// module of that name: it would turn on test order rather than on behaviour. A fresh process per case
/// is the only way to make these claims honestly.
/// </remarks>
internal static class NativeProbe
{
    internal sealed record Result(int ExitCode, string Output);

    internal static Result Run(string pluginRoot, string pluginName, string mode)
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // The test host is itself dotnet, so its own path runs the probe. Falling back to the name on
        // PATH covers a host that is something else.
        if (
            !Path.GetFileNameWithoutExtension(start.FileName)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        )
        {
            start.FileName = "dotnet";
        }

        start.ArgumentList.Add(PluginFixtures.NativeProbeHost);
        start.ArgumentList.Add(pluginRoot);
        start.ArgumentList.Add(pluginName);
        start.ArgumentList.Add(mode);

        using Process process =
            Process.Start(start) ?? throw new AssertionException("The native probe did not start.");

        StringBuilder output = new();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());

        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new AssertionException($"The native probe did not finish. Output so far: {output}");
        }

        return new Result(process.ExitCode, output.ToString());
    }
}

/// <summary>
/// The override exists for a native asset that sits where the manifest declares it. A copy flattened
/// beside the entry assembly is found by the runtime's own probing either way, so a call that succeeds
/// against that layout proves nothing about the override.
/// </summary>
[TestFixture]
public class Given_a_plugin_whose_native_asset_sits_where_its_manifest_declares_it
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.NativePortable);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_resolves_and_calls_the_library_through_the_loader()
    {
        NativeProbe.Result result = NativeProbe.Run(
            _root.RootPath,
            PluginFixtures.NativePortable,
            mode: "loader"
        );

        result.Output.Should().Contain("LOAD SUCCEEDED");
        result.Output.Should().Contain("NATIVE RESULT 3");
        result.ExitCode.Should().Be(0);
    }

    [Test]
    public void It_cannot_resolve_the_library_without_the_unmanaged_override()
    {
        // The control, and the reason the case above is worth anything. The same plugin, the same
        // layout, and a context that resolves managed assemblies host-first exactly as the real one
        // does while overriding nothing for unmanaged ones: the call fails. That is what shows the
        // override is doing the work rather than the runtime's default probing.
        NativeProbe.Result result = NativeProbe.Run(
            _root.RootPath,
            PluginFixtures.NativePortable,
            mode: "plain"
        );

        result.Output.Should().Contain("LOAD SUCCEEDED");
        result.Output.Should().Contain("NATIVE FAILURE System.DllNotFoundException");
    }
}

[TestFixture]
public class Given_a_plugin_whose_native_asset_was_flattened_beside_the_entry_assembly
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Native);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_resolves_without_the_override_too_which_is_why_this_layout_proves_nothing_about_it()
    {
        // Recorded rather than assumed. A runtime-specific publish relocates the running identifier's
        // native asset to the plugin root, where the runtime's own probing finds it, so a passing call
        // against this layout says nothing about whether the override ran. This is the measurement that
        // makes the declared-layout tests above the load-bearing ones.
        NativeProbe.Result result = NativeProbe.Run(_root.RootPath, PluginFixtures.Native, mode: "plain");

        result.Output.Should().Contain("LOAD SUCCEEDED");
        result.Output.Should().Contain("NATIVE RESULT 3");
    }
}

[TestFixture]
public class Given_a_plugin_whose_declared_native_asset_is_not_there
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Native);

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
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_and_then_fails_at_first_use()
    {
        // The design records a missing native asset as a lazy first-use failure rather than a load-time
        // check, because a manifest describes native assets per runtime identifier and the runtime
        // resolves them lazily. Both halves of that are asserted here, in a process where no module of
        // this name has been loaded, which is the only place the second half means anything.
        NativeProbe.Result result = NativeProbe.Run(_root.RootPath, PluginFixtures.Native, mode: "loader");

        result.Output.Should().Contain("LOAD SUCCEEDED");
        result.Output.Should().Contain("NATIVE FAILURE System.DllNotFoundException");
        result.ExitCode.Should().Be(0);
    }
}
