// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// Each way an allowlisted plugin can fail to compose, asserted on the exception escaping host
/// creation and on the phase, state, error type and error message the startup status file recorded.
/// </summary>
/// <remarks>
/// <para>
/// The assertion is deliberately not that <c>IStartupProcessExit</c> was invoked. Plugin loading runs
/// before the container exists, so nothing DI-registered has been resolved yet; a test that passed by
/// substituting that interface would be passing for a reason that has nothing to do with this phase.
/// What is asserted instead is that host creation threw and no request was ever served.
/// </para>
/// <para>
/// The error type and message are asserted rather than assumed present. That file is what an operator
/// and a container orchestrator read from outside a process that never finished starting, so a phase
/// and a state alone would leave them knowing that plugin loading failed and nothing about which
/// plugin or why. Each case below names the value that distinguishes it from the other two.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_AnAllowlistedPluginCannotBeComposed
{
    /// <summary>
    /// What the <c>Acme.HookThrows</c> fixture throws from its hook, as a literal.
    /// </summary>
    /// <remarks>
    /// The constant that declares it lives in the fixture plugin, which this project stages as bytes
    /// under a plugin root rather than referencing, so there is nothing to read it from here. Asserting
    /// it is what proves the inner failure reached the status file instead of being flattened into the
    /// wrapper's own text.
    /// </remarks>
    private const string HookThrowsFailureMessage = "Acme.HookThrows could not read its own configuration";

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
        ReadErrorType(statusPath).Should().Be(nameof(PluginLoadException));
        ReadErrorMessage(statusPath)
            .Should()
            .Contain(
                "Acme.DmsContributer",
                "the misspelling is the only thing that tells an operator what to fix"
            )
            .And.Contain("is allowlisted but");
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
        ReadErrorType(statusPath).Should().Be(nameof(PluginLoadException));
        ReadErrorMessage(statusPath)
            .Should()
            .Contain(
                // The loader resolves the configured root before it names it, so the assertion
                // compares against the same normalization rather than against the raw setting.
                Path.GetFullPath(absentRoot),
                "the operator has to be told which root to create"
            )
            .And.Contain("does not exist");
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
        ReadErrorType(statusPath)
            .Should()
            .Be(
                nameof(PluginCompositionException),
                "a throwing hook is refused by the composition phase, so what the status file records "
                    + "is the refusal and not the plugin's own exception type"
            );
        ReadErrorMessage(statusPath)
            .Should()
            .Contain("Acme.HookThrows", "the refusal names the plugin the operator has to remove or fix")
            .And.Contain("ContributeServices")
            .And.Contain(
                HookThrowsFailureMessage,
                "the wrapper carries the inner message forward, which is the only account of what the "
                    + "plugin was actually unable to do"
            );
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

    private static string? ReadErrorType(string statusPath) =>
        PluginHostProbe.ReadStartupStatus(statusPath)["ErrorType"]?.GetValue<string>();

    private static string? ReadErrorMessage(string statusPath) =>
        PluginHostProbe.ReadStartupStatus(statusPath)["ErrorMessage"]?.GetValue<string>();
}
