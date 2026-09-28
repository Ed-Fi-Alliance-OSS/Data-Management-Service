// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// A configuration manager shaped like a host's: the empty in-memory source every
/// <see cref="ConfigurationManager"/> starts with, a source standing in for the JSON files, the
/// operator's environment, and a source above it standing in for the command line.
/// </summary>
/// <remarks>
/// The sources are kept by reference so a case can assert exactly where each ended up. The real host
/// list, with real JSON, environment and command-line sources, is asserted against in the Data
/// Management Service's frontend tests, which can build one.
/// </remarks>
internal sealed class ConfigurationProbeHost
{
    /// <summary>A prefix no environment variable carries, so the source reads nothing.</summary>
    private const string UnusedEnvironmentPrefix = "EDFI_PLUGIN_CONFIGURATION_TESTS_UNUSED_";

    internal ConfigurationProbeHost(IReadOnlyDictionary<string, string?> json)
    {
        Json = new MemoryConfigurationSource { InitialData = json };
        Environment = new EnvironmentVariablesConfigurationSource { Prefix = UnusedEnvironmentPrefix };
        CommandLine = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["Fixture:Host:CommandLine"] = "present" },
        };

        // A new manager already holds one empty in-memory source, which is where an indexer write lands.
        // A host's does too, so it is kept rather than cleared.
        Default = Manager.Sources.Single();

        Manager.Sources.Add(Json);
        Manager.Sources.Add(Environment);
        Manager.Sources.Add(CommandLine);
    }

    internal ConfigurationManager Manager { get; } = new();

    internal IConfigurationSource Default { get; }

    internal MemoryConfigurationSource Json { get; }

    internal EnvironmentVariablesConfigurationSource Environment { get; }

    internal MemoryConfigurationSource CommandLine { get; }

    /// <summary>A host whose JSON stand-in tells each named plugin what its hook should do.</summary>
    internal static ConfigurationProbeHost Directing(params (string Plugin, string Behavior)[] behaviors) =>
        new(
            behaviors.ToDictionary(
                pair => $"Fixture:{pair.Plugin}:Configuration",
                string? (pair) => pair.Behavior
            )
        );

    /// <summary>
    /// Whether <paramref name="source"/> is the one source the loader inserted for the plugin with
    /// <paramref name="pluginName"/>.
    /// </summary>
    internal static bool IsSuppliedBy(IConfigurationSource source, string pluginName) =>
        source is ChainedConfigurationSource { Configuration: { } configuration }
        && configuration[$"Fixture:Supplied:{pluginName}"] == pluginName;

    /// <summary>The types of the providers inside the source the loader inserted for a plugin.</summary>
    internal static IEnumerable<Type> ProviderTypesIn(IConfigurationSource source) =>
        ((IConfigurationRoot)((ChainedConfigurationSource)source).Configuration!).Providers.Select(provider =>
            provider.GetType()
        );
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_appends_configuration_sources
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationProbeHost _host = null!;
    private StringWriter _diagnostics = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        _host = ConfigurationProbeHost.Directing((PluginFixtures.ConfigContributor, "append"));
        _diagnostics = new StringWriter();

        plugins.ContributeConfiguration(_host.Manager, _diagnostics);
    }

    [TearDown]
    public void TearDown()
    {
        _diagnostics.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_announces_the_plugin_on_the_diagnostic_channel()
    {
        _diagnostics
            .ToString()
            .Should()
            .Be(
                $"invoking ContributeConfiguration on {PluginFixtures.ConfigContributor}{Environment.NewLine}"
            );
    }

    [Test]
    public void It_hands_the_hook_a_builder_other_than_its_bootstrap_configuration()
    {
        FixtureObservations
            .Read($"{PluginFixtures.ConfigContributor}:builderIsBootstrap")
            .Should()
            .Be("False");
    }

    [Test]
    public void It_inserts_one_source_immediately_below_the_environment_source()
    {
        IList<IConfigurationSource> sources = _host.Manager.Sources;

        // Exactly five: the four the host had and the one the loader inserted for the plugin's two.
        sources.Should().HaveCount(5);
        sources[0].Should().BeSameAs(_host.Default);
        sources[1].Should().BeSameAs(_host.Json);
        ConfigurationProbeHost.IsSuppliedBy(sources[2], PluginFixtures.ConfigContributor).Should().BeTrue();
        sources[3].Should().BeSameAs(_host.Environment);
        sources[4].Should().BeSameAs(_host.CommandLine);
    }

    [Test]
    public void It_carries_both_of_the_plugins_sources_in_the_inserted_one()
    {
        ConfigurationProbeHost
            .ProviderTypesIn(_host.Manager.Sources[2])
            .Should()
            .Equal(typeof(MemoryConfigurationProvider), typeof(MemoryConfigurationProvider));
    }

    [Test]
    public void It_keeps_the_plugins_later_source_winning_over_its_earlier_one()
    {
        _host.Manager["Fixture:WithinPlugin"].Should().Be($"{PluginFixtures.ConfigContributor}:second");
    }

    [Test]
    public void It_resolves_a_key_only_the_plugin_supplies()
    {
        _host
            .Manager[$"Fixture:Supplied:{PluginFixtures.ConfigContributor}"]
            .Should()
            .Be(PluginFixtures.ConfigContributor);
    }
}

/// <summary>
/// A plugin that adds an environment source of its own, after an ordinary source.
/// </summary>
/// <remarks>
/// The placement anchor is the last environment source present when the phase began. Recomputed after
/// the hook, it would be the plugin's own environment source, and the plugin's ordinary source would be
/// placed just below that one while the plugin's environment source stayed above the operator's
/// environment and command line.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_adds_its_own_environment_source
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationProbeHost _host = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        _host = ConfigurationProbeHost.Directing((PluginFixtures.ConfigContributor, "appendEnvironment"));

        plugins.ContributeConfiguration(_host.Manager, new StringWriter());
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_places_both_added_sources_below_the_hosts_environment_source_in_their_order()
    {
        IList<IConfigurationSource> sources = _host.Manager.Sources;

        sources.Should().HaveCount(5);
        sources[0].Should().BeSameAs(_host.Default);
        sources[1].Should().BeSameAs(_host.Json);
        ConfigurationProbeHost.IsSuppliedBy(sources[2], PluginFixtures.ConfigContributor).Should().BeTrue();
        ConfigurationProbeHost
            .ProviderTypesIn(sources[2])
            .Should()
            .Equal(typeof(MemoryConfigurationProvider), typeof(EnvironmentVariablesConfigurationProvider));
        sources[3].Should().BeSameAs(_host.Environment);
        sources[4].Should().BeSameAs(_host.CommandLine);
    }
}

/// <summary>
/// An addition made at the bottom of the list rather than on top of it.
/// </summary>
/// <remarks>
/// It removes and moves nothing that was there before, so it is an addition like an append, and the
/// placement step puts it where every plugin source goes.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_inserts_a_source_below_the_host_sources
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationProbeHost _host = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        _host = ConfigurationProbeHost.Directing((PluginFixtures.ConfigContributor, "insert"));

        plugins.ContributeConfiguration(_host.Manager, new StringWriter());
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_moves_the_source_immediately_below_the_environment_source()
    {
        IList<IConfigurationSource> sources = _host.Manager.Sources;

        sources.Should().HaveCount(5);
        sources[0].Should().BeSameAs(_host.Default);
        sources[1].Should().BeSameAs(_host.Json);
        ConfigurationProbeHost.IsSuppliedBy(sources[2], PluginFixtures.ConfigContributor).Should().BeTrue();
        sources[3].Should().BeSameAs(_host.Environment);
        sources[4].Should().BeSameAs(_host.CommandLine);
    }

    [Test]
    public void It_lets_the_plugin_value_win_over_the_json_value()
    {
        // The JSON stand-in carries the behaviour key and nothing that competes, so this reads the
        // plugin's own key: had the insert been left at index zero, the result would be the same, which
        // is why the placement itself is asserted above by identity.
        _host
            .Manager[$"Fixture:Supplied:{PluginFixtures.ConfigContributor}"]
            .Should()
            .Be(PluginFixtures.ConfigContributor);
    }
}

/// <summary>
/// A plugin that sets a base path on its own builder and names a JSON file relative to it.
/// </summary>
/// <remarks>
/// A file source takes its file provider from the builder it is built with, so the plugin's sources have
/// to be built with the properties the hook left on its staging builder. Built with the host's, the file
/// is looked for under the host's base path, where it is not.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_reads_a_file_relative_to_its_own_base_path
{
    private const string PluginValue = "from-the-plugins-own-directory";

    private TemporaryPluginRoot _root = null!;
    private DirectoryInfo _hostBasePath = null!;
    private DirectoryInfo _pluginBasePath = null!;
    private ConfigurationProbeHost _host = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        _hostBasePath = Directory.CreateTempSubdirectory("edfi-plugin-host-base-");
        _pluginBasePath = Directory.CreateTempSubdirectory("edfi-plugin-own-base-");
        File.WriteAllText(
            Path.Combine(_pluginBasePath.FullName, "plugin.json"),
            $$"""{ "Fixture": { "FromPluginFile": "{{PluginValue}}" } }"""
        );

        _host = new ConfigurationProbeHost(
            new Dictionary<string, string?>
            {
                [$"Fixture:{PluginFixtures.ConfigContributor}:Configuration"] = "appendJsonFromBasePath",
                [$"Fixture:{PluginFixtures.ConfigContributor}:BasePath"] = _pluginBasePath.FullName,
            }
        );

        // A base path of the host's own, holding no plugin.json, so the file resolves only if the
        // plugin's base path is the one used.
        _host.Manager.SetBasePath(_hostBasePath.FullName);

        plugins.ContributeConfiguration(_host.Manager, new StringWriter());
    }

    [TearDown]
    public void TearDown()
    {
        _host.Manager.Dispose();
        _pluginBasePath.Delete(recursive: true);
        _hostBasePath.Delete(recursive: true);
        _root.Dispose();
    }

    [Test]
    public void It_reads_the_file_from_the_plugins_base_path()
    {
        _host.Manager["Fixture:FromPluginFile"].Should().Be(PluginValue);
    }

    [Test]
    public void It_leaves_the_hosts_base_path_as_it_was()
    {
        ((IConfigurationBuilder)_host.Manager)
            .GetFileProvider()
            .Should()
            .BeOfType<Microsoft.Extensions.FileProviders.PhysicalFileProvider>()
            .Which.Root.Should()
            .Be(_hostBasePath.FullName + Path.DirectorySeparatorChar);
    }
}

/// <summary>
/// A host with no environment source at all, so the placement anchor is missing.
/// </summary>
/// <remarks>
/// DMS always has one, so this is the fallback rather than a shape a real host takes: the plugin's source
/// goes on top of the list, above a command-line source.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_host_with_no_environment_source
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationManager _manager = null!;
    private IConfigurationSource _default = null!;
    private MemoryConfigurationSource _json = null!;
    private MemoryConfigurationSource _commandLine = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        _manager = new ConfigurationManager();
        _default = _manager.Sources.Single();
        _json = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                [$"Fixture:{PluginFixtures.ConfigContributor}:Configuration"] = "append",
            },
        };
        _commandLine = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["Fixture:Precedence:CommandLine"] = "command-line",
            },
        };
        _manager.Sources.Add(_json);
        _manager.Sources.Add(_commandLine);

        plugins.ContributeConfiguration(_manager, new StringWriter());
    }

    [TearDown]
    public void TearDown()
    {
        _manager.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_puts_the_plugin_source_on_top_of_the_list()
    {
        IList<IConfigurationSource> sources = _manager.Sources;

        sources.Should().HaveCount(4);
        sources[0].Should().BeSameAs(_default);
        sources[1].Should().BeSameAs(_json);
        sources[2].Should().BeSameAs(_commandLine);
        ConfigurationProbeHost.IsSuppliedBy(sources[3], PluginFixtures.ConfigContributor).Should().BeTrue();
    }

    [Test]
    public void It_lets_the_plugin_value_win_over_the_command_line_value()
    {
        _manager["Fixture:Precedence:CommandLine"].Should().Be(PluginFixtures.ConfigContributor);
    }
}

/// <summary>
/// Two plugins, allowlisted in the order opposite to their names, so allowlist order and name order
/// cannot be mistaken for each other.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_two_plugins_that_append_configuration_sources
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationProbeHost _host = null!;
    private StringWriter _diagnostics = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(
            _root,
            PluginFixtures.SecondConfigContributor,
            PluginFixtures.ConfigContributor
        );

        _host = ConfigurationProbeHost.Directing(
            (PluginFixtures.SecondConfigContributor, "append"),
            (PluginFixtures.ConfigContributor, "append")
        );
        _diagnostics = new StringWriter();

        plugins.ContributeConfiguration(_host.Manager, _diagnostics);
    }

    [TearDown]
    public void TearDown()
    {
        _diagnostics.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_invokes_the_hooks_in_allowlist_order()
    {
        int first = int.Parse(
            FixtureObservations.Read($"{PluginFixtures.SecondConfigContributor}:enteredAt")!
        );
        int second = int.Parse(FixtureObservations.Read($"{PluginFixtures.ConfigContributor}:enteredAt")!);

        first.Should().BeLessThan(second);
    }

    [Test]
    public void It_announces_each_plugin_in_allowlist_order()
    {
        _diagnostics
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Should()
            .Equal(
                $"invoking ContributeConfiguration on {PluginFixtures.SecondConfigContributor}",
                $"invoking ContributeConfiguration on {PluginFixtures.ConfigContributor}"
            );
    }

    [Test]
    public void It_places_the_later_plugins_sources_above_the_earlier_plugins()
    {
        IList<IConfigurationSource> sources = _host.Manager.Sources;

        sources.Should().HaveCount(6);
        sources[0].Should().BeSameAs(_host.Default);
        sources[1].Should().BeSameAs(_host.Json);
        ConfigurationProbeHost
            .IsSuppliedBy(sources[2], PluginFixtures.SecondConfigContributor)
            .Should()
            .BeTrue();
        ConfigurationProbeHost.IsSuppliedBy(sources[3], PluginFixtures.ConfigContributor).Should().BeTrue();
        sources[4].Should().BeSameAs(_host.Environment);
        sources[5].Should().BeSameAs(_host.CommandLine);
    }

    [Test]
    public void It_lets_the_later_plugin_read_the_earlier_plugins_value_through_its_bootstrap_configuration()
    {
        FixtureObservations.Read($"{PluginFixtures.SecondConfigContributor}:winnerOnEntry").Should().Be("");
        FixtureObservations
            .Read($"{PluginFixtures.ConfigContributor}:winnerOnEntry")
            .Should()
            .Be(PluginFixtures.SecondConfigContributor);
    }

    [Test]
    public void It_lets_the_later_plugin_in_the_allowlist_win()
    {
        _host.Manager["Fixture:Winner"].Should().Be(PluginFixtures.ConfigContributor);
        _host.Manager["Fixture:WithinPlugin"].Should().Be($"{PluginFixtures.ConfigContributor}:second");
    }

    [Test]
    public void It_still_resolves_the_earlier_plugins_own_key()
    {
        _host
            .Manager[$"Fixture:Supplied:{PluginFixtures.SecondConfigContributor}"]
            .Should()
            .Be(PluginFixtures.SecondConfigContributor);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_removes_a_pre_existing_configuration_source
{
    private TemporaryPluginRoot _root = null!;
    private StringWriter _diagnostics = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        ConfigurationProbeHost host = ConfigurationProbeHost.Directing(
            (PluginFixtures.ConfigContributor, "remove")
        );
        _diagnostics = new StringWriter();

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeConfiguration(host.Manager, _diagnostics)
        )!;
    }

    [TearDown]
    public void TearDown()
    {
        _diagnostics.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_fails_naming_the_plugin_and_the_source()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ConfigurationSourceRemoved);
        _failure.PluginName.Should().Be(PluginFixtures.ConfigContributor);
        _failure
            .Message.Should()
            .StartWith(
                $"plugin '{PluginFixtures.ConfigContributor}' removed configuration source 0 "
                    + $"({typeof(MemoryConfigurationSource).FullName})"
            );
    }

    [Test]
    public void It_reports_the_refusal_on_the_diagnostic_channel()
    {
        _diagnostics.ToString().Should().Contain($"plugin configuration refused: {_failure.Message}");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_reorders_pre_existing_configuration_sources
{
    private TemporaryPluginRoot _root = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        ConfigurationProbeHost host = ConfigurationProbeHost.Directing(
            (PluginFixtures.ConfigContributor, "reorder")
        );

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeConfiguration(host.Manager, new StringWriter())
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_fails_naming_the_plugin_and_the_source_that_moved()
    {
        // The fixture swaps the first two sources, the manager's default and the JSON stand-in. Walking
        // the list in order, the JSON stand-in at position 1 is the first one no longer where it was.
        _failure.Reason.Should().Be(PluginCompositionFailure.ConfigurationSourceReordered);
        _failure.PluginName.Should().Be(PluginFixtures.ConfigContributor);
        _failure
            .Message.Should()
            .StartWith(
                $"plugin '{PluginFixtures.ConfigContributor}' moved configuration source 1 "
                    + $"({typeof(MemoryConfigurationSource).FullName})"
            );
    }
}

/// <summary>
/// Two plugins whose sources count their own loads, standing in for vault-backed sources.
/// </summary>
/// <remarks>
/// A <see cref="ConfigurationManager"/> reloads every source on every change to its list. Adding each
/// plugin's sources to it directly and then moving them loaded the first plugin's four times and the
/// second's twice; building each plugin's additions once and inserting them as one source loads each
/// once, however many plugins follow.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_two_plugins_whose_sources_count_their_loads
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationProbeHost _host = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(
            _root,
            PluginFixtures.ConfigContributor,
            PluginFixtures.SecondConfigContributor
        );

        _host = ConfigurationProbeHost.Directing(
            (PluginFixtures.ConfigContributor, "appendCounted"),
            (PluginFixtures.SecondConfigContributor, "appendCounted")
        );

        plugins.ContributeConfiguration(_host.Manager, new StringWriter());
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_the_earlier_plugins_source_once()
    {
        FixtureObservations.CountOf($"{PluginFixtures.ConfigContributor}:loads").Should().Be(1);
    }

    [Test]
    public void It_loads_the_later_plugins_source_once()
    {
        FixtureObservations.CountOf($"{PluginFixtures.SecondConfigContributor}:loads").Should().Be(1);
    }

    [Test]
    public void It_still_resolves_the_later_plugins_counted_value()
    {
        _host.Manager["Fixture:Counted"].Should().Be($"{PluginFixtures.SecondConfigContributor}:loads");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_source_that_fails_to_load
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationProbeHost _host = null!;
    private StringWriter _diagnostics = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        _host = ConfigurationProbeHost.Directing((PluginFixtures.ConfigContributor, "appendFailing"));
        _diagnostics = new StringWriter();

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeConfiguration(_host.Manager, _diagnostics)
        )!;
    }

    [TearDown]
    public void TearDown()
    {
        _diagnostics.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_fails_naming_the_plugin()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ConfigurationSourceLoadFailed);
        _failure.PluginName.Should().Be(PluginFixtures.ConfigContributor);
        _failure
            .Message.Should()
            .StartWith(
                $"plugin '{PluginFixtures.ConfigContributor}' added a configuration source from "
                    + "ContributeConfiguration that failed to load"
            );
    }

    [Test]
    public void It_keeps_the_original_exception()
    {
        _failure
            .InnerException.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Be($"{PluginFixtures.ConfigContributor}:loads could not reach its vault");
    }

    [Test]
    public void It_leaves_the_host_sources_unchanged()
    {
        _host.Manager.Sources.Should().HaveCount(4);
        _host.Manager.Sources.Should().NotContain(source => source is ChainedConfigurationSource);
    }

    [Test]
    public void It_reports_the_refusal_on_the_diagnostic_channel()
    {
        _diagnostics.ToString().Should().Contain($"plugin configuration refused: {_failure.Message}");
    }
}

/// <summary>
/// A later plugin removing a source an earlier plugin added. By the time the later hook runs that
/// source is pre-existing, so the removal is refused like any other.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_a_later_plugin_that_removes_an_earlier_plugins_configuration_source
{
    private TemporaryPluginRoot _root = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(
            _root,
            PluginFixtures.ConfigContributor,
            PluginFixtures.SecondConfigContributor
        );

        ConfigurationProbeHost host = ConfigurationProbeHost.Directing(
            (PluginFixtures.ConfigContributor, "append"),
            (PluginFixtures.SecondConfigContributor, "removePluginSource")
        );

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeConfiguration(host.Manager, new StringWriter())
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_fails_naming_the_later_plugin()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ConfigurationSourceRemoved);
        _failure.PluginName.Should().Be(PluginFixtures.SecondConfigContributor);
        _failure
            .Message.Should()
            .StartWith(
                $"plugin '{PluginFixtures.SecondConfigContributor}' removed configuration source 2 "
                    + $"({typeof(ChainedConfigurationSource).FullName})"
            );
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_configuration_hook_that_throws
{
    private TemporaryPluginRoot _root = null!;
    private SequencedDiagnostics _diagnostics = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.ConfigContributor);

        ConfigurationProbeHost host = ConfigurationProbeHost.Directing(
            (PluginFixtures.ConfigContributor, "throw")
        );
        _diagnostics = new SequencedDiagnostics("invoking ContributeConfiguration");

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeConfiguration(host.Manager, _diagnostics)
        )!;
    }

    [TearDown]
    public void TearDown()
    {
        _diagnostics.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_announced_the_plugin()
    {
        _diagnostics
            .ToString()
            .Should()
            .Contain($"invoking ContributeConfiguration on {PluginFixtures.ConfigContributor}");
    }

    /// <summary>
    /// Ordered against the number the hook takes on entry, for the reason the service phase's case
    /// gives: presence alone cannot tell an announcement on entry from one written on the way out.
    /// </summary>
    [Test]
    public void It_announced_the_plugin_before_the_hook_was_entered()
    {
        int? announcedAt = _diagnostics.AnnouncedAt;
        string? enteredAt = FixtureObservations.Read($"{PluginFixtures.ConfigContributor}:enteredAt");

        announcedAt.Should().NotBeNull("the channel should have carried the announcement");
        enteredAt.Should().NotBeNull("the hook should have run");
        announcedAt!.Value.Should().BeLessThan(int.Parse(enteredAt!));
    }

    [Test]
    public void It_becomes_a_fatal_naming_the_plugin_and_the_configuration_phase()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ContributeConfigurationThrew);
        _failure.PluginName.Should().Be(PluginFixtures.ConfigContributor);
        _failure
            .Message.Should()
            .StartWith($"plugin '{PluginFixtures.ConfigContributor}' threw from ContributeConfiguration");
    }

    [Test]
    public void It_keeps_the_original_exception()
    {
        _failure.InnerException.Should().BeOfType<InvalidOperationException>();
        _failure
            .InnerException!.Message.Should()
            .Be($"{PluginFixtures.ConfigContributor} could not reach its configuration store");
    }

    [Test]
    public void It_reports_the_failure_on_the_diagnostic_channel()
    {
        _diagnostics
            .ToString()
            .Should()
            .Contain($"{PluginFixtures.ConfigContributor} could not reach its configuration store");
    }
}

/// <summary>
/// A plugin source that supplies <c>Plugins:Allowed</c>, run through the same manager the loader read
/// the allowlist from.
/// </summary>
/// <remarks>
/// The allowlist was consumed to decide what loaded before any configuration hook ran, so nothing a
/// hook contributes can reach it. The case shows the attempt is real, by reading the key back as the
/// plugin's value, and that the loaded set is unchanged regardless.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_source_that_supplies_the_allowlist
{
    private TemporaryPluginRoot _root = null!;
    private ConfigurationProbeHost _host = null!;
    private LoadedPlugins _plugins = null!;
    private IReadOnlyList<LoadedPlugin> _loadedBefore = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.ConfigContributor);

        _host = new ConfigurationProbeHost(
            new Dictionary<string, string?>
            {
                ["Plugins:Directory"] = _root.RootPath,
                ["Plugins:Allowed"] = PluginFixtures.ConfigContributor,
                [$"Fixture:{PluginFixtures.ConfigContributor}:Configuration"] = "append",
            }
        );

        _plugins = PluginLoader.Load(_host.Manager, PluginLoaderProbe.Contracts, new StringWriter());
        _loadedBefore = [.. _plugins.Plugins];

        _plugins.ContributeConfiguration(_host.Manager, new StringWriter());
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_lets_the_plugin_source_change_what_the_key_now_reads()
    {
        _host.Manager["Plugins:Allowed"].Should().Be("Acme.NotAllowlisted");
    }

    [Test]
    public void It_leaves_the_loaded_plugins_exactly_as_the_allowlist_decided()
    {
        _plugins.Plugins.Should().Equal(_loadedBefore);
        _plugins.Plugins.Select(plugin => plugin.Name).Should().Equal(PluginFixtures.ConfigContributor);
    }
}
