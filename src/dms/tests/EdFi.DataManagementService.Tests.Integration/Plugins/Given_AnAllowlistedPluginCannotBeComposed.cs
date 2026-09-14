// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// Each way an allowlisted plugin can fail to compose, asserted on the exception escaping host
/// creation and on the phase the startup status file recorded.
/// </summary>
/// <remarks>
/// The assertion is deliberately not that <c>IStartupProcessExit</c> was invoked. Plugin loading runs
/// before the container exists, so nothing DI-registered has been resolved yet; a test that passed by
/// substituting that interface would be passing for a reason that has nothing to do with this phase.
/// What is asserted instead is that host creation threw and no request was ever served.
/// </remarks>
public sealed class Given_AnAllowlistedPluginCannotBeComposed
{
    private readonly List<string> _paths = [];

    [TearDown]
    public void TearDown()
    {
        foreach (string path in _paths)
        {
            PluginHostProbe.DeleteIfPresent(path);
        }

        _paths.Clear();
    }

    [Test]
    public void It_fails_host_creation_and_records_LoadPlugins_when_the_allowlist_entry_is_misspelled()
    {
        (Exception? failure, string statusPath) = BootWith(
            PluginHostProbe.CreatePluginRoot("Acme.DmsContributor"),
            allowed: "Acme.DmsContributer"
        );

        failure.Should().NotBeNull("the loader cannot resolve a directory that is not there");
        ReadPhase(statusPath).Should().Be("LoadPlugins");
        ReadState(statusPath).Should().Be("Failed");
    }

    [Test]
    public void It_fails_host_creation_and_records_LoadPlugins_when_the_plugin_root_is_missing()
    {
        string absentRoot = Path.Combine(
            Path.GetTempPath(),
            "dms-plugin-integration",
            $"absent-{Guid.NewGuid():N}"
        );

        (Exception? failure, string statusPath) = BootWith(absentRoot, allowed: "Acme.DmsContributor");

        failure.Should().NotBeNull("a non-empty allowlist over a root that does not exist is fatal");
        ReadPhase(statusPath).Should().Be("LoadPlugins");
        ReadState(statusPath).Should().Be("Failed");
    }

    [Test]
    public void It_fails_host_creation_and_records_ConfigureServices_when_a_hook_throws()
    {
        (Exception? failure, string statusPath) = BootWith(
            PluginHostProbe.CreatePluginRoot("Acme.HookThrows"),
            allowed: "Acme.HookThrows"
        );

        failure.Should().NotBeNull();
        ReadPhase(statusPath)
            .Should()
            .Be(
                "ConfigureServices",
                "the contribution phase is invoked from inside AddServices, so the failure lands in "
                    + "whichever bootstrap phase the host was running rather than in LoadPlugins"
            );
        ReadState(statusPath).Should().Be("Failed");
    }

    /// <summary>
    /// Boots the host and returns whatever escaped, plus the startup status file it wrote.
    /// </summary>
    private (Exception? Failure, string StatusPath) BootWith(string pluginRoot, string allowed)
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-fatal-{Guid.NewGuid():N}.json"
        );

        _paths.Add(pluginRoot);
        _paths.Add(statusPath);

        WebApplicationFactory<Program> factory = PluginHostProbe.CreateHost(
            fixture,
            pluginRoot,
            allowed,
            statusPath,
            new PluginLogCapture()
        );

        try
        {
            // Creating a client is what builds the host. Nothing is served: the exception below is
            // raised while the entry point is still running its bootstrap phases.
            using HttpClient client = factory.CreateClient();

            return (null, statusPath);
        }
        catch (Exception exception)
        {
            return (exception, statusPath);
        }
        finally
        {
            factory.Dispose();
        }
    }

    private static string? ReadPhase(string statusPath) =>
        PluginHostProbe.ReadStartupStatus(statusPath)["Phase"]?.GetValue<string>();

    private static string? ReadState(string statusPath) =>
        PluginHostProbe.ReadStartupStatus(statusPath)["State"]?.GetValue<string>();
}
