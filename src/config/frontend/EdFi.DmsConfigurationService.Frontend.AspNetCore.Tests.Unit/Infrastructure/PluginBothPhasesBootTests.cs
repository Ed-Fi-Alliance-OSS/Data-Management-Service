// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// A real Configuration Service boot with the both-phases fixture plugin allowlisted: its
/// configuration source supplies a value the host reads, its service hook registers a secret
/// resolver, and the host emits one inventory event for it.
/// </summary>
/// <remarks>
/// <para>
/// The fixture is published into this project's output by PluginCmsFixtures.targets and is never
/// referenced by this assembly, so every expectation about it is written out here as a literal rather
/// than read from its types: a reference would load the fixture into the default context and make the
/// type identities under test meaningless.
/// </para>
/// <para>
/// NonParallelizable because the loader writes to the process-wide Console.Error, which this fixture
/// redirects for the length of the boot so the diagnostic channel can be searched too.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_host_booted_with_the_both_phases_fixture_plugin_allowlisted
{
    private const string PluginName = "Acme.CmsContributor";
    private const string PluginSwaggerUiOrigin = "https://acme-cms-contributor.example";
    private const string PluginConfigurationSourceType =
        "Acme.CmsContributor.CmsContributorConfigurationSource";
    private const string SecretLookingKey = "AcmeVault:ClientSecret";
    private const string SecretLookingValue = "acme-vault-7f3c9e1b-plugin-secret";

    private static readonly string _pluginRoot = Path.Combine(AppContext.BaseDirectory, "PluginFixtures");

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingLoggerProvider _logs = null!;
    private string _diagnostics = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _logs = new CapturingLoggerProvider();

        TextWriter originalError = Console.Error;
        using StringWriter error = new();
        Console.SetError(error);

        try
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                // UseSetting writes into the host's configuration ahead of Program's first line, which
                // is where the loader reads the plugin root and the allowlist.
                builder.UseSetting("Plugins:Directory", _pluginRoot);
                builder.UseSetting("Plugins:Allowed", PluginName);
                builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_logs));
            });

            // WebApplicationFactory defers the entry point until the server is first needed.
            using HttpClient client = _factory.CreateClient();
        }
        finally
        {
            Console.SetError(originalError);
            _diagnostics = error.ToString();
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
        _logs.Dispose();
    }

    private IReadOnlyList<CapturedLog> InventoryEvents =>
        [.. _logs.Entries.Where(entry => entry.EventId.Name == "PluginInventory")];

    [Test]
    public void It_announces_the_plugin_on_the_diagnostic_channel()
    {
        _diagnostics.Should().Contain($"invoking ContributeConfiguration on {PluginName}");
    }

    [Test]
    public void It_resolves_the_configuration_value_the_plugin_source_supplies()
    {
        _factory
            .Services.GetRequiredService<IConfiguration>()["Cors:SwaggerUIOrigin"]
            .Should()
            .Be(PluginSwaggerUiOrigin);
    }

    [Test]
    public void It_builds_the_hosts_cors_policy_from_the_plugin_supplied_origin()
    {
        _factory
            .Services.GetRequiredService<IOptions<CorsOptions>>()
            .Value.GetPolicy("AllowSwaggerUI")!
            .Origins.Should()
            .Equal(PluginSwaggerUiOrigin);
    }

    [Test]
    public void It_resolves_the_plugins_secret_resolver_from_the_container()
    {
        ISecretResolver resolver = _factory.Services.GetRequiredService<ISecretResolver>();

        resolver.GetType().FullName.Should().Be("Acme.CmsContributor.CmsContributorSecretResolver");
        resolver.GetType().Assembly.GetName().Name.Should().Be(PluginName);
    }

    [Test]
    public void It_resolves_the_same_resolver_instance_every_time()
    {
        _factory
            .Services.GetRequiredService<ISecretResolver>()
            .Should()
            .BeSameAs(_factory.Services.GetRequiredService<ISecretResolver>());
    }

    [Test]
    public void It_records_one_contribution_from_the_plugin_including_its_configuration()
    {
        PluginContributionRecord record = _factory
            .Services.GetRequiredService<PluginAuditInput>()
            .Records.Should()
            .ContainSingle()
            .Subject;

        record.PluginName.Should().Be(PluginName);
        record.ContributedConfiguration.Should().BeTrue();
    }

    [Test]
    public void It_emits_one_information_inventory_event_for_the_plugin()
    {
        InventoryEvents
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<CapturedLog>(entry =>
                entry.Level == LogLevel.Information && (string?)entry.State["PluginName"] == PluginName
            );
    }

    [Test]
    public void It_carries_every_required_inventory_property()
    {
        InventoryEvents
            .Should()
            .ContainSingle()
            .Which.State.Keys.Should()
            .Contain([
                "PluginName",
                "AssemblyVersion",
                "@DeclaredFiles",
                "@RegisteredServiceTypes",
                "@RemovedDescriptors",
                "@HostFirstSubstitutions",
            ]);
    }

    [Test]
    public void It_lists_the_plugins_entry_assembly_among_the_declared_files()
    {
        InventoryEvents
            .Single()
            .State["@DeclaredFiles"]
            .Should()
            .BeAssignableTo<IEnumerable<PluginInventoryFileEntry>>()
            .Which.Select(file => file.FileName)
            .Should()
            .Contain($"{PluginName}.dll");
    }

    [Test]
    public void It_lists_the_resolver_and_the_hosted_service_among_the_registered_services()
    {
        InventoryEvents
            .Single()
            .State["@RegisteredServiceTypes"]
            .Should()
            .BeAssignableTo<IEnumerable<PluginRegisteredServiceEntry>>()
            .Which.Should()
            .ContainEquivalentOf(
                new PluginRegisteredServiceEntry(
                    typeof(ISecretResolver).FullName!,
                    "Acme.CmsContributor.CmsContributorSecretResolver",
                    "Singleton",
                    false
                )
            )
            .And.ContainEquivalentOf(
                new PluginRegisteredServiceEntry(
                    typeof(IHostedService).FullName!,
                    "Acme.CmsContributor.CmsContributorHostedService",
                    "Singleton",
                    false
                )
            );
    }

    [Test]
    public void It_names_the_type_of_the_source_the_plugin_added_in_its_inventory_event()
    {
        InventoryEvents
            .Single()
            .State["@ConfigurationSourceTypes"]
            .Should()
            .BeAssignableTo<IEnumerable<string>>()
            .Which.Should()
            .Equal(PluginConfigurationSourceType);
    }

    [Test]
    public void It_writes_the_type_of_the_source_the_plugin_added()
    {
        CapturedText()
            .Contains(PluginConfigurationSourceType, StringComparison.Ordinal)
            .Should()
            .BeTrue("the Phase A record names the source type a plugin added");
    }

    [Test]
    public void It_does_not_write_the_secret_looking_configuration_key_or_value_anywhere()
    {
        // Asserted as booleans so that a failure reports which one leaked without printing the
        // captured text, which would put the value into the test output.
        string captured = CapturedText();

        captured
            .Contains(SecretLookingKey, StringComparison.Ordinal)
            .Should()
            .BeFalse("the key must not be logged");
        captured
            .Contains(SecretLookingValue, StringComparison.Ordinal)
            .Should()
            .BeFalse("the value must not be logged");
    }

    /// <summary>
    /// Everything this boot put on a channel an operator reads: the diagnostic channel, and every log
    /// record's structured state rendered as JSON so nested projections are searched too.
    /// </summary>
    private string CapturedText() =>
        _diagnostics
        + string.Join(
            '\n',
            _logs.Entries.Select(entry =>
                JsonSerializer.Serialize(
                    entry.State.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString())
                )
                + JsonSerializer.Serialize(entry.State.Values)
                + entry.Exception
            )
        );
}
