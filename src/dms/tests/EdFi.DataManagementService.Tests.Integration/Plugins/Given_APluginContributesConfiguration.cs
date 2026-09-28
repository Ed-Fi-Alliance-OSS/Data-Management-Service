// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// Boots the host with and without the configuration contributor, so the two can be compared.
/// </summary>
internal sealed class PhaseAHostRun : IAsyncDisposable
{
    public const string ConfigContributor = "Acme.ConfigContributor";

    /// <summary>What the fixture supplies for the encryption key, from its own name.</summary>
    public const string PluginEncryptionKey = $"{ConfigContributor}-encryption-key";

    private readonly string _pluginRoot;
    private readonly string _startupStatusFilePath;

    private PhaseAHostRun(
        WebApplicationFactory<Program> factory,
        string pluginRoot,
        string startupStatusFilePath,
        HttpStatusCode metadataStatus
    )
    {
        Factory = factory;
        _pluginRoot = pluginRoot;
        _startupStatusFilePath = startupStatusFilePath;
        MetadataStatus = metadataStatus;
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpStatusCode MetadataStatus { get; }

    public string StartupState =>
        PluginHostProbe.ReadStartupStatus(_startupStatusFilePath)["State"]?.GetValue<string>() ?? "";

    /// <summary>The host's configuration sources, in precedence order, lowest first.</summary>
    public IReadOnlyList<IConfigurationSource> Sources =>
        [.. ((IConfigurationBuilder)Factory.Services.GetRequiredService<IConfiguration>()).Sources];

    /// <summary>
    /// Boots the host. With <paramref name="allowlistContributor"/> the fixture is allowlisted and
    /// told to append its sources; without it nothing is allowlisted, which is the baseline.
    /// </summary>
    public static async Task<PhaseAHostRun> StartAsync(bool allowlistContributor)
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);

        string pluginRoot = allowlistContributor
            ? PluginHostProbe.CreatePluginRoot(ConfigContributor)
            : PluginHostProbe.CreatePluginRoot();
        string startupStatusFilePath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-phase-a-{Guid.NewGuid():N}.json"
        );

        WebApplicationFactory<Program> factory = PluginHostProbe.CreateHost(
            fixture,
            pluginRoot,
            allowed: allowlistContributor ? ConfigContributor : string.Empty,
            startupStatusFilePath,
            new PluginLogCapture(),
            new Dictionary<string, string> { [$"Fixture:{ConfigContributor}:Configuration"] = "append" }
        );

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/metadata");

        return new PhaseAHostRun(factory, pluginRoot, startupStatusFilePath, response.StatusCode);
    }

    /// <summary>Whether a source is one of the two the fixture adds.</summary>
    public static bool IsPluginSource(IConfigurationSource source) =>
        source is MemoryConfigurationSource { InitialData: { } data }
        && data.Any(pair =>
            pair.Key == $"Fixture:Supplied:{ConfigContributor}" || pair.Key == "Fixture:WithinPlugin"
        );

    /// <summary>
    /// What identifies a source across two boots: its type and the property that says what it reads.
    /// </summary>
    /// <remarks>
    /// Two builders make distinct source objects, so identity cannot be compared across them, and the
    /// in-memory sources carry per-boot values such as the plugin root. The type with the JSON path or
    /// the environment prefix is what an added, removed or moved source would change.
    /// </remarks>
    public static string Describe(IConfigurationSource source) =>
        source switch
        {
            _ when IsPluginSource(source) => PluginSourceDescription,
            JsonConfigurationSource json => $"{source.GetType().Name} {json.Path}",
            EnvironmentVariablesConfigurationSource environment =>
                $"{source.GetType().Name} prefix '{environment.Prefix}'",
            _ => source.GetType().Name,
        };

    /// <summary>What <see cref="Describe"/> writes for either of the plugin's two sources.</summary>
    public const string PluginSourceDescription = "plugin source";

    /// <summary>
    /// The source list the host is expected to end with, built without the loader: the sources
    /// <c>WebApplication.CreateBuilder</c> installs for this application and environment, plus the one
    /// environment source <c>AddServices</c> appends, with the plugin's two sources, when there are any,
    /// immediately below the last environment source <c>CreateBuilder</c> installed.
    /// </summary>
    /// <remarks>
    /// Built from a real builder rather than written down, so the runtime's own source list is what the
    /// host is compared against. <c>WebApplicationFactory</c> passes its settings as command-line
    /// arguments, so the builder is given its application name and environment the same way, which is
    /// also what makes its two command-line sources appear as they do in the host.
    /// </remarks>
    public static IReadOnlyList<string> ExpectedSources(bool withPluginSources)
    {
        // As command-line arguments rather than WebApplicationOptions properties, the way
        // WebApplicationFactory passes them: setting the properties installs an in-memory source of
        // their own that the host under test does not have.
        WebApplicationBuilder builder = WebApplication.CreateBuilder([
            $"--applicationName={typeof(Program).Assembly.GetName().Name}",
            "--environment=Test",
        ]);

        try
        {
            List<string> expected =
            [
                .. ((IConfigurationBuilder)builder.Configuration).Sources.Select(Describe),
            ];
            int lastEnvironment = expected.FindLastIndex(description =>
                description.StartsWith(
                    nameof(EnvironmentVariablesConfigurationSource),
                    StringComparison.Ordinal
                )
            );

            if (withPluginSources)
            {
                expected.InsertRange(lastEnvironment, [PluginSourceDescription, PluginSourceDescription]);
            }

            // AddServices' own AddEnvironmentVariables, which runs after the configuration phase.
            expected.Add($"{nameof(EnvironmentVariablesConfigurationSource)} prefix ''");

            return expected;
        }
        finally
        {
            builder.Configuration.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        PluginHostProbe.DeleteIfPresent(_pluginRoot);
        PluginHostProbe.DeleteIfPresent(_startupStatusFilePath);
    }
}

/// <summary>
/// A plugin whose only contribution is a configuration source supplying
/// <c>ConfigurationServiceSettings:EncryptionKey</c>, over a host whose Test JSON also supplies one.
/// </summary>
/// <remarks>
/// Both source lists, and a baseline host's with nothing allowlisted, are compared against the list a
/// bare <c>WebApplication.CreateBuilder</c> produces, which the loader has no part in: the only
/// difference allowed is the plugin's two sources, in the one place placement puts them.
/// </remarks>
[Category("PluginIntegration")]
[NonParallelizable]
public sealed class Given_APluginContributesTheEncryptionKey
{
    private PhaseAHostRun _baseline = null!;
    private PhaseAHostRun _run = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _baseline = await PhaseAHostRun.StartAsync(allowlistContributor: false);
        _run = await PhaseAHostRun.StartAsync(allowlistContributor: true);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _run.DisposeAsync();
        await _baseline.DisposeAsync();
    }

    [Test]
    public void It_boots_and_serves_requests()
    {
        _run.MetadataStatus.Should().Be(HttpStatusCode.OK);
        _run.StartupState.Should().Be("Ready");
    }

    [Test]
    public void It_resolves_the_plugins_value_in_the_options_DMS_binds()
    {
        _run.Factory.Services.GetRequiredService<IOptions<ConfigurationServiceSettings>>()
            .Value.EncryptionKey.Should()
            .Be(PhaseAHostRun.PluginEncryptionKey);
    }

    [Test]
    public void It_resolves_the_test_json_value_without_the_plugin()
    {
        // The premise: without the plugin the same key resolves to what appsettings.Test.json carries,
        // so the case above is the plugin outranking a real JSON value.
        _baseline
            .Factory.Services.GetRequiredService<IOptions<ConfigurationServiceSettings>>()
            .Value.EncryptionKey.Should()
            .Be("TestEncryptionKey123456789012345678901234567890");
    }

    [Test]
    public void It_records_the_plugin_as_a_configuration_contributor()
    {
        PluginContributionRecord record = _run
            .Factory.Services.GetRequiredService<PluginAuditInput>()
            .Records.Should()
            .ContainSingle()
            .Subject;

        record.PluginName.Should().Be(PhaseAHostRun.ConfigContributor);
        record.ContributedConfiguration.Should().BeTrue();
        record.Additions.Should().BeEmpty("the fixture registers no service");
    }

    [Test]
    public void It_ends_with_exactly_the_expected_sources_and_no_source_of_the_loaders_own()
    {
        // The plugin's two sources, immediately below the last environment source CreateBuilder
        // installed and so below the command-line source above it, and nothing else added anywhere.
        _run.Sources.Select(PhaseAHostRun.Describe)
            .Should()
            .Equal(PhaseAHostRun.ExpectedSources(withPluginSources: true));
    }

    [Test]
    public void It_ends_with_exactly_the_expected_sources_when_nothing_is_allowlisted()
    {
        _baseline
            .Sources.Select(PhaseAHostRun.Describe)
            .Should()
            .Equal(PhaseAHostRun.ExpectedSources(withPluginSources: false));
    }
}

/// <summary>
/// The same plugin, with the operator also setting the encryption key in the environment.
/// </summary>
[Category("PluginIntegration")]
[NonParallelizable]
public sealed class Given_APluginContributesAKeyTheEnvironmentAlsoSets
{
    private const string EnvironmentVariable = "ConfigurationServiceSettings__EncryptionKey";
    private const string EnvironmentValue = "environment-encryption-key";

    private string? _priorValue;
    private PhaseAHostRun _run = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _priorValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, EnvironmentValue);

        _run = await PhaseAHostRun.StartAsync(allowlistContributor: true);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _run.DisposeAsync();
        Environment.SetEnvironmentVariable(EnvironmentVariable, _priorValue);
    }

    [Test]
    public void It_boots_and_serves_requests()
    {
        _run.MetadataStatus.Should().Be(HttpStatusCode.OK);
        _run.StartupState.Should().Be("Ready");
    }

    [Test]
    public void It_resolves_the_environment_value_over_the_plugins()
    {
        _run.Factory.Services.GetRequiredService<IOptions<ConfigurationServiceSettings>>()
            .Value.EncryptionKey.Should()
            .Be(EnvironmentValue);
    }

    [Test]
    public void It_still_loaded_the_plugins_sources()
    {
        // So the case above is the environment winning over a plugin value that is present, rather
        // than a plugin that contributed nothing.
        _run.Sources.Count(PhaseAHostRun.IsPluginSource).Should().Be(2);
    }
}
