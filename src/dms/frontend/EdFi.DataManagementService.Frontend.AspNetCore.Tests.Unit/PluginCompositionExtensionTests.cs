// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.CustomValidation;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The seam that carries the plugin composition phase's output across <c>builder.Build()</c>, and the
/// cases that need the Data Management Service's own populated collection and its own contract types.
/// </summary>
internal static class PluginCompositionProbe
{
    /// <summary>The staged fixture plugin that contributes against the host's own contracts.</summary>
    internal const string DmsContributor = "Acme.DmsContributor";

    /// <summary>An audit input carrying nothing, for the cases that only read the registrations.</summary>
    internal static PluginAuditInput EmptyInput() => new(new PluginContractRegistry([]), [], []);

    /// <summary>
    /// A collection the Data Management Service's own <c>AddServices</c> has populated, which is the
    /// only place the real repository and validator descriptors exist to be displaced or admitted.
    /// </summary>
    internal static IServiceCollection DmsPopulatedCollection()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Test" }
        );

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AppSettings:Datastore"] = "postgresql",
                ["AppSettings:MaskRequestBodyInLogs"] = "false",
                ["AppSettings:MaximumPageSize"] = "500",
                ["AppSettings:DefaultPartitionCount"] = "10",
                ["ConfigurationServiceSettings:BaseUrl"] = "https://example.org",
                ["ConfigurationServiceSettings:ClientId"] = "client-id",
                ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                ["ConfigurationServiceSettings:Scope"] = "scope",
                ["ConfigurationServiceSettings:EncryptionKey"] =
                    "TestEncryptionKey123456789012345678901234567890",
            }
        );

        builder.AddServices();

        return builder.Services;
    }

    /// <summary>Configuration a plugin hook reads its behaviour from.</summary>
    internal static IConfiguration HookConfiguration(string? behavior) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fixture:Behavior"] = behavior })
            .Build();

    /// <summary>
    /// Loads the staged fixture plugin from a plugin root of the test's own, through the real loader.
    /// </summary>
    internal static LoadedPlugins Load(string pluginRoot, string fixtureName = DmsContributor)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "PluginFixtures", fixtureName);
        string destination = Path.Combine(pluginRoot, fixtureName);

        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        "{\"Plugins\": {\"Directory\": "
                            + JsonSerializer.Serialize(pluginRoot)
                            + ", \"Allowed\": "
                            + JsonSerializer.Serialize(fixtureName)
                            + "}}"
                    )
                )
            )
            .Build();

        return PluginLoader.Load(
            configuration,
            ["EdFi.Api.Plugins", "EdFi.DataManagementService.CustomValidation"]
        );
    }

    internal static string CreatePluginRoot() =>
        Directory
            .CreateDirectory(
                Path.Combine(Path.GetTempPath(), "dms-plugin-frontend-tests", Guid.NewGuid().ToString("N"))
            )
            .FullName;

    internal static void DeletePluginRoot(string pluginRoot)
    {
        try
        {
            if (Directory.Exists(pluginRoot))
            {
                Directory.Delete(pluginRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A loaded plugin assembly is held by a non-collectible context for the life of the
            // process, so its file can still be locked. Leaving a temporary directory behind is not
            // worth failing a passing test over, which is the same conclusion the plugin hosting
            // tests' own temporary root reached.
        }
    }
}

[TestFixture]
public class Given_the_plugin_audit_registration_seam
{
    private ServiceCollection _services = null!;
    private PluginAuditInput _input = null!;

    [SetUp]
    public void Setup()
    {
        _services = new ServiceCollection();
        _input = PluginCompositionProbe.EmptyInput();

        _services.AddPluginAudit(_input);
    }

    /// <summary>
    /// Exactly two registrations, and this is the only place they exist, so nothing can duplicate the
    /// ordering they depend on.
    /// </summary>
    [Test]
    public void It_registers_exactly_two_descriptors()
    {
        _services.Should().HaveCount(2);
    }

    /// <summary>
    /// An instance rather than a type, so the container activates nothing to hand it over, and so the
    /// value the composition phase produced is exactly what the check reads.
    /// </summary>
    [Test]
    public void It_registers_the_audit_input_as_the_instance_it_was_given()
    {
        _services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(PluginAuditInput))
            .Which.ImplementationInstance.Should()
            .BeSameAs(_input);
    }

    [Test]
    public void It_registers_the_startup_check()
    {
        _services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(IDmsStartupTask))
            .Which.ImplementationType!.Name.Should()
            .Be("PluginRegistrationGuard");
    }

    /// <summary>
    /// The check takes its input by constructor and never reads the service collection out of the
    /// container, which a plugin is permitted to register its own of.
    /// </summary>
    [Test]
    public void It_registers_no_service_collection()
    {
        _services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IServiceCollection));
    }
}

[TestFixture]
public class Given_the_startup_check_the_seam_registered
{
    private ServiceProvider _provider = null!;
    private IDmsStartupTask _task = null!;

    [SetUp]
    public void Setup()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddPluginAudit(PluginCompositionProbe.EmptyInput());

        _provider = services.BuildServiceProvider();
        _task = _provider.GetServices<IDmsStartupTask>().Single();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    /// <summary>
    /// Inside the 200-299 window Program.cs executes, and above the custom validator guard at 250, so
    /// these checks read a collection that audit has already accepted.
    /// </summary>
    [Test]
    public void It_runs_inside_the_executed_window_and_above_the_custom_validator_guard()
    {
        _task.Order.Should().BeInRange(200, DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum);
        _task.Order.Should().BeGreaterThan(250);
        _task.Order.Should().Be(260);
    }

    [Test]
    public async Task It_reports_nothing_when_no_plugin_contributed()
    {
        Func<Task> execution = () => _task.ExecuteAsync(CancellationToken.None);

        // Awaited. The assertion returns a Task, and dropping it leaves an asynchronous failure
        // unobserved, which NUnit reports as a pass.
        await execution.Should().NotThrowAsync();
    }
}

[TestFixture]
public class Given_a_host_that_has_added_its_own_services
{
    private IServiceCollection _services = null!;

    [SetUp]
    public void Setup() => _services = PluginCompositionProbe.DmsPopulatedCollection();

    /// <summary>
    /// Called once and unconditionally from AddServices, whose signature is unchanged, over the empty
    /// set of loaded plugins until the host-integration story wires the loader in.
    /// </summary>
    [Test]
    public void It_registered_the_audit_input_exactly_once()
    {
        _services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(PluginAuditInput));
    }

    [Test]
    public void It_registered_the_plugin_startup_check_exactly_once()
    {
        // Filtered before asserting rather than inside the assertion: FluentAssertions takes an
        // expression tree there, and a pattern match is not allowed in one.
        _services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IDmsStartupTask)
                && descriptor.ImplementationType is not null
                && descriptor.ImplementationType.Name == "PluginRegistrationGuard"
            )
            .Should()
            .ContainSingle();
    }

    [Test]
    public void It_registered_no_service_collection_anywhere()
    {
        _services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IServiceCollection));
    }

    /// <summary>
    /// The descriptors the real host-type cases displace. If the host stopped registering these the
    /// cases below would pass for the wrong reason, so their presence is asserted rather than assumed.
    /// </summary>
    [Test]
    public void It_registered_the_repository_the_plugin_cases_try_to_displace()
    {
        _services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IDocumentStoreRepository));
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_its_own_audit_input
{
    private string _pluginRoot = null!;
    private ServiceProvider _provider = null!;
    private PluginAuditInput _resolved = null!;

    [SetUp]
    public void Setup()
    {
        _pluginRoot = PluginCompositionProbe.CreatePluginRoot();
        LoadedPlugins plugins = PluginCompositionProbe.Load(_pluginRoot);

        ServiceCollection services = new();
        services.AddLogging();

        // Driven through the production seam, so the ordering under test is the ordering production
        // uses rather than one the test arranged.
        services.AddPluginServiceContributions(
            PluginCompositionProbe.HookConfiguration("decoyAudit"),
            plugins
        );

        _provider = services.BuildServiceProvider();
        _resolved = _provider.GetRequiredService<PluginAuditInput>();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        PluginCompositionProbe.DeletePluginRoot(_pluginRoot);
    }

    /// <summary>
    /// The plugin's own registration is on the collection, so this is a real contest rather than an
    /// arrangement in which only one candidate exists.
    /// </summary>
    [Test]
    public void It_is_one_of_two_registrations_for_that_type()
    {
        _provider.GetServices<PluginAuditInput>().Should().HaveCount(2);
    }

    /// <summary>
    /// The host's wins, because the host registers after every hook has run and a single-service
    /// resolve takes the last registration. The plugin's carries no records and an empty registry; the
    /// host's carries this plugin's record and the contract the host declares.
    /// </summary>
    [Test]
    public void It_loses_to_the_input_the_host_registered()
    {
        _resolved.Records.Should().ContainSingle().Which.PluginName.Should().Be("Acme.DmsContributor");
        _resolved
            .Registry.Entries.Should()
            .ContainSingle()
            .Which.Contract.Should()
            .Be(typeof(ICustomResourceValidator));
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_the_real_custom_resource_validator
{
    private string _pluginRoot = null!;

    [SetUp]
    public void Setup() => _pluginRoot = PluginCompositionProbe.CreatePluginRoot();

    [TearDown]
    public void TearDown() => PluginCompositionProbe.DeletePluginRoot(_pluginRoot);

    /// <summary>
    /// The contract's own service type is declared in the assembly
    /// EdFi.DataManagementService.CustomValidation, so it matches the host-owned test on assembly name
    /// alone. It is admitted only because the declared-contract exemption is evaluated first.
    /// </summary>
    [Test]
    public async Task It_is_admitted_under_the_hosts_own_registry()
    {
        LoadedPlugins plugins = PluginCompositionProbe.Load(_pluginRoot);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddPluginServiceContributions(PluginCompositionProbe.HookConfiguration(null), plugins);

        await using ServiceProvider provider = services.BuildServiceProvider();
        PluginAuditInput input = provider.GetRequiredService<PluginAuditInput>();

        PluginAuditResult result = await PluginRegistrationAudit.AuditAsync(input, provider);

        result.Findings.Should().BeEmpty();
        provider.GetServices<ICustomResourceValidator>().Should().ContainSingle();
    }

    /// <summary>
    /// The identical registration, audited against a registry that does not declare the contract. That
    /// it becomes fatal is what shows the exemption is what admitted it above, rather than some
    /// property of the type.
    /// </summary>
    [Test]
    public async Task It_is_refused_once_the_registry_does_not_declare_it()
    {
        LoadedPlugins plugins = PluginCompositionProbe.Load(_pluginRoot);
        ServiceCollection services = new();
        services.AddLogging();

        PluginContractRegistry registryWithoutTheContract = new([]);
        PluginAuditInput input = plugins.ContributeServices(
            services,
            PluginCompositionProbe.HookConfiguration(null),
            registryWithoutTheContract
        );

        await using ServiceProvider provider = services.BuildServiceProvider();
        PluginAuditResult result = await PluginRegistrationAudit.AuditAsync(input, provider);

        result
            .Findings.Should()
            .Contain(finding => finding.Reason == PluginAuditFailure.HostOwnedServiceTypeClaimed);
        result
            .Findings.Should()
            .Contain(finding => finding.Message.Contains(nameof(ICustomResourceValidator)));
    }
}
