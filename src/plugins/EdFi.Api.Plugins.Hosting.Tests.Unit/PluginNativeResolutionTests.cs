// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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
    /// <summary>How long a probe may run before the runner ends it.</summary>
    /// <remarks>
    /// Generous, because the work is starting a process and loading a plugin and a build machine can be
    /// slow at both. It bounds a hang; it is not a performance assertion. A case about the bound itself
    /// passes a short one.
    /// </remarks>
    private static readonly TimeSpan _defaultDeadline = TimeSpan.FromMinutes(2);

    internal static ChildProcess.Result Run(
        string pluginRoot,
        string pluginName,
        string mode,
        TimeSpan? deadline = null
    )
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

        return ChildProcess.Run(start, deadline ?? _defaultDeadline, "The native probe");
    }
}

/// <summary>
/// The runner's own two failure modes, which every native assertion here depends on not having.
/// </summary>
/// <remarks>
/// Both belong to the runner rather than to the loader, and both are invisible in a passing run: a
/// probe that hangs and a probe that fills a pipe look exactly like a slow one until the suite itself
/// stops. They are asserted against a child that really behaves that way, with a short deadline so the
/// cases cost seconds.
/// </remarks>
[TestFixture]
public class Given_a_probe_process_the_runner_cannot_simply_read_to_the_end
{
    [Test]
    public void It_ends_a_probe_that_never_finishes_at_the_deadline()
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        AssertionException failure = Assert.Throws<AssertionException>(() =>
            NativeProbe.Run(
                PluginFixtures.Root,
                PluginFixtures.Good,
                mode: "stall",
                deadline: TimeSpan.FromSeconds(5)
            )
        )!;

        elapsed.Stop();

        failure.Message.Should().Contain("did not finish within its deadline");

        // The claim is that the deadline is what ended it. Well below the runner's own default, so a
        // slow machine cannot make this pass for the wrong reason.
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Test]
    public void It_stops_waiting_for_streams_a_departed_probe_left_open()
    {
        // The probe exits immediately, having started a process that inherited its streams. Waiting for
        // the streams to end rather than for the process to exit is what makes the captured output
        // complete, and waiting for them without a bound is what makes that wait never return.
        Stopwatch elapsed = Stopwatch.StartNew();

        AssertionException failure = Assert.Throws<AssertionException>(() =>
            NativeProbe.Run(
                PluginFixtures.Root,
                PluginFixtures.Good,
                mode: "linger",
                deadline: TimeSpan.FromSeconds(5)
            )
        )!;

        elapsed.Stop();

        failure.Message.Should().Contain("held its streams open past the deadline");

        // What it did manage to read is reported rather than thrown away.
        failure.Message.Should().Contain("LINGER STARTED");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Test]
    public void It_finishes_a_probe_that_fills_its_error_stream_before_writing_its_output()
    {
        // Two megabytes of error output ahead of a single line of ordinary output, which is far more
        // than a pipe holds. A runner that reads output to its end first never sees that line.
        ChildProcess.Result result = NativeProbe.Run(
            PluginFixtures.Root,
            PluginFixtures.Good,
            mode: "spew",
            deadline: TimeSpan.FromSeconds(60)
        );

        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain("SPEW DONE");
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
        ChildProcess.Result result = NativeProbe.Run(
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
        ChildProcess.Result result = NativeProbe.Run(
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
        ChildProcess.Result result = NativeProbe.Run(_root.RootPath, PluginFixtures.Native, mode: "plain");

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
        ChildProcess.Result result = NativeProbe.Run(_root.RootPath, PluginFixtures.Native, mode: "loader");

        result.Output.Should().Contain("LOAD SUCCEEDED");
        result.Output.Should().Contain("NATIVE FAILURE System.DllNotFoundException");
        result.ExitCode.Should().Be(0);
    }
}
