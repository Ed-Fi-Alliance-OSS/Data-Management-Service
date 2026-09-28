// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The configuration phase over the source list <see cref="WebApplication.CreateBuilder(WebApplicationOptions)"/>
/// really installs, with the host's own <c>appsettings.json</c>, a real environment variable, and a
/// real command-line argument, driven the way <c>Program.cs</c> drives it.
/// </summary>
/// <remarks>
/// A plugin that appends a source would land above both of the operator's explicit surfaces if its
/// sources were left where it put them. These are the three outcomes the placement exists for, read
/// through the configuration a host would read them from.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_configuration_source_over_the_hosts_real_source_list
{
    private const string ConfigContributor = "Acme.ConfigContributor";
    private const string EncryptionKey = "ConfigurationServiceSettings:EncryptionKey";
    private const string EnvironmentKey = "Fixture:Precedence:Environment";
    private const string EnvironmentVariable = "Fixture__Precedence__Environment";
    private const string CommandLineKey = "Fixture:Precedence:CommandLine";

    private string _pluginRoot = null!;
    private string? _priorEnvironmentValue;
    private WebApplicationBuilder _builder = null!;
    private List<IConfigurationSource> _before = null!;
    private string? _encryptionKeyBefore;

    [SetUp]
    public void Setup()
    {
        _pluginRoot = PluginCompositionProbe.CreatePluginRoot();
        PluginCompositionProbe.Stage(_pluginRoot, ConfigContributor);

        _priorEnvironmentValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, "environment");

        _builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                // The host's own content root, so appsettings.json is the file DMS ships, with its empty
                // encryption key. Production, so no appsettings.Development.json or user secrets join it.
                ContentRootPath = Path.Combine(
                    FrontendStagingProbe.RepositoryRoot,
                    "dms",
                    "frontend",
                    "EdFi.DataManagementService.Frontend.AspNetCore"
                ),
                EnvironmentName = "Production",
                Args =
                [
                    $"--Plugins:Directory={_pluginRoot}",
                    $"--Plugins:Allowed={ConfigContributor}",
                    $"--Fixture:{ConfigContributor}:Configuration=append",
                    $"--{CommandLineKey}=command-line",
                ],
            }
        );

        _before = [.. ((IConfigurationBuilder)_builder.Configuration).Sources];
        _encryptionKeyBefore = _builder.Configuration[EncryptionKey];

        // The two calls Program.cs makes inside its LoadPlugins phase, in its order.
        LoadedPlugins plugins = PluginLoader.Load(
            _builder.Configuration,
            DmsPluginContracts.Registry.ContractAssemblyNames
        );
        plugins.ContributeConfiguration(_builder.Configuration);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, _priorEnvironmentValue);
        _builder.Configuration.Dispose();
        PluginCompositionProbe.DeletePluginRoot(_pluginRoot);
    }

    [Test]
    public void It_starts_from_the_empty_string_the_host_ships()
    {
        // The premise of the first outcome below, asserted so that case cannot pass against a host
        // that stopped shipping the key.
        _encryptionKeyBefore.Should().Be(string.Empty);
    }

    [Test]
    public void It_resolves_the_plugin_value_over_the_empty_string_in_appsettings()
    {
        _builder.Configuration[EncryptionKey].Should().Be($"{ConfigContributor}-encryption-key");
    }

    [Test]
    public void It_resolves_an_environment_value_over_the_plugin_value()
    {
        _builder.Configuration[EnvironmentKey].Should().Be("environment");
    }

    [Test]
    public void It_resolves_a_command_line_value_over_the_plugin_value()
    {
        _builder.Configuration[CommandLineKey].Should().Be("command-line");
    }

    [Test]
    public void It_places_the_plugin_sources_immediately_below_the_last_environment_source()
    {
        List<IConfigurationSource> after = [.. ((IConfigurationBuilder)_builder.Configuration).Sources];
        int lastEnvironment = _before.FindLastIndex(source =>
            source is EnvironmentVariablesConfigurationSource
        );

        // The loader added no source of its own: the host's sources, in their order, with exactly one
        // inserted where the last environment source was, carrying the plugin's two, and nothing else.
        after.Should().HaveCount(_before.Count + 1);
        after.Take(lastEnvironment).Should().Equal(_before.Take(lastEnvironment), ReferenceEquals);
        after.Skip(lastEnvironment + 1).Should().Equal(_before.Skip(lastEnvironment), ReferenceEquals);
        _before.Should().NotContain(after[lastEnvironment]);
        after[lastEnvironment]
            .Should()
            .BeOfType<ChainedConfigurationSource>()
            .Which.Configuration.Should()
            .BeAssignableTo<IConfigurationRoot>()
            .Which.Providers.Should()
            .HaveCount(2);
    }
}
