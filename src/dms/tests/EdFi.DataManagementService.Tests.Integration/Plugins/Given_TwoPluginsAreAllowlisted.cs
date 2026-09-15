// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.CustomValidation;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// The real host boots with two allowlisted plugins, and everything the host-integration story claims
/// about that boot is asserted against the one run.
/// </summary>
/// <remarks>
/// One boot for all of it, deliberately: each assertion below is about the same process reaching the
/// same state, and booting the host once per assertion would multiply a slow arrange for no added
/// evidence.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TwoPluginsAreAllowlisted
{
    private const string ContributorPlugin = "Acme.DmsContributor";
    private const string HookTouchPlugin = "Acme.DmsHookTouch";

    /// <summary>
    /// A value only configuration carries, so the no-configuration-in-the-inventory criterion is
    /// asserted against something that would actually be there if the rule were broken.
    /// </summary>
    private const string SecretProbeValue = "plugin-inventory-must-not-carry-this-8d41f2";

    /// <summary>A directory under the root that no allowlist entry names.</summary>
    private const string UnallowlistedDirectory = "Acme.NotAllowlisted";

    private WebApplicationFactory<Program>? _factory;
    private PluginLogCapture _capture = new();
    private string? _pluginRoot;
    private string? _startupStatusFilePath;
    private string? _observedServiceTypesPath;
    private string? _observationPath;
    private HttpResponseMessage? _metadataResponse;
    private string[] _observedServiceTypes = [];
    private string _loaderDiagnostics = string.Empty;

    [OneTimeSetUp]
    public async Task Setup()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);

        _pluginRoot = PluginHostProbe.CreatePluginRoot(ContributorPlugin, HookTouchPlugin);

        // A directory nobody allowlisted, whose entry assembly is not an assembly. A loader that
        // opened it would fail the boot, so reaching Ready is what proves it was only named.
        PluginHostProbe.WriteCorruptPluginDirectory(_pluginRoot, UnallowlistedDirectory);
        _startupStatusFilePath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-startup-{Guid.NewGuid():N}.json"
        );
        _observedServiceTypesPath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-observed-{Guid.NewGuid():N}.txt"
        );
        _observationPath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-touch-{Guid.NewGuid():N}.txt"
        );
        _capture = new PluginLogCapture();

        _factory = PluginHostProbe.CreateHost(
            fixture,
            _pluginRoot,
            $"{ContributorPlugin},{HookTouchPlugin}",
            _startupStatusFilePath,
            _capture,
            new Dictionary<string, string>
            {
                ["Fixture:ObservedServiceTypesPath"] = _observedServiceTypesPath,
                ["Fixture:ObservationPath"] = _observationPath,
                ["Fixture:SecretProbe"] = SecretProbeValue,
                ["Fixture:Behavior"] = "removeFrameworkDescriptor",
            }
        );

        // The loader writes its own diagnostics to Console.Error, because it necessarily runs before
        // the logger exists. It reads the writer at call time, so redirecting it around the boot is
        // what makes those lines readable here.
        TextWriter originalError = Console.Error;
        StringWriter loaderDiagnostics = new();
        Console.SetError(loaderDiagnostics);

        try
        {
            using HttpClient client = _factory.CreateClient();
            _metadataResponse = await client.GetAsync("/metadata");
        }
        finally
        {
            Console.SetError(originalError);
        }

        _loaderDiagnostics = loaderDiagnostics.ToString();

        _observedServiceTypes = File.Exists(_observedServiceTypesPath)
            ? await File.ReadAllLinesAsync(_observedServiceTypesPath)
            : [];
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _metadataResponse?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        PluginHostProbe.DeleteIfPresent(_pluginRoot);
        PluginHostProbe.DeleteIfPresent(_startupStatusFilePath);
        PluginHostProbe.DeleteIfPresent(_observedServiceTypesPath);
        PluginHostProbe.DeleteIfPresent(_observationPath);
    }

    [Test]
    public void It_serves_requests()
    {
        _metadataResponse!.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_reached_the_ready_phase()
    {
        PluginHostProbe
            .ReadStartupStatus(_startupStatusFilePath!)["State"]
            ?.GetValue<string>()
            .Should()
            .Be("Ready");
    }

    [Test]
    public void It_resolves_a_validator_only_a_plugin_could_have_registered()
    {
        using IServiceScope scope = _factory!.Services.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IEnumerable<ICustomResourceValidator>>()
            .Select(validator => validator.GetType().FullName)
            .Should()
            .Contain("Acme.DmsContributor.FixtureResourceValidator")
            .And.Contain("Acme.DmsHookTouch.HookTouchResourceValidator");
    }

    [Test]
    public void It_gave_the_startup_task_a_record_for_each_plugin()
    {
        _factory!
            .Services.GetRequiredService<PluginAuditInput>()
            .Records.Select(record => record.PluginName)
            .Should()
            .BeEquivalentTo(ContributorPlugin, HookTouchPlugin);
    }

    [Test]
    public void It_ran_the_registration_guard_over_those_records()
    {
        // The guard's own acceptance line, which it writes only after auditing what it was given. A
        // resolvable PluginAuditInput would prove registration; this proves the task read it.
        _capture
            .Events.Where(logEvent =>
                logEvent.MessageTemplate.Text.StartsWith(
                    "Plugin registration guard accepted",
                    StringComparison.Ordinal
                )
            )
            .Should()
            .ContainSingle()
            .Which.Properties["PluginCount"]
            .ToString()
            .Should()
            .Be("2");
    }

    [Test]
    public void It_emitted_one_inventory_event_for_each_plugin()
    {
        _capture
            .InventoryEvents.Select(logEvent => PluginLogCapture.ScalarText(logEvent, "PluginName"))
            .Should()
            .BeEquivalentTo(ContributorPlugin, HookTouchPlugin);
    }

    [Test]
    public void It_carried_the_named_properties_on_the_inventory_event()
    {
        LogEvent inventory = InventoryFor(HookTouchPlugin);

        inventory
            .Properties.Keys.Should()
            .Contain([
                "PluginName",
                "AssemblyVersion",
                "DeclaredFiles",
                "RegisteredServiceTypes",
                "RemovedDescriptors",
                "HostFirstSubstitutions",
            ]);
    }

    [Test]
    public void It_destructured_the_declared_files_rather_than_rendering_them()
    {
        LogEvent inventory = InventoryFor(HookTouchPlugin);
        IReadOnlyList<LogEventPropertyValue> files = PluginLogCapture.Sequence(inventory, "DeclaredFiles");

        files.Should().NotBeEmpty();
        files.Should().AllBeOfType<StructureValue>();
        PluginLogCapture
            .Member(files[0], "FileName")
            .Should()
            .NotBeNullOrEmpty("every declared file row carries the file name it was published under");
    }

    [Test]
    public void It_reported_the_dependency_the_hook_touched_as_loaded()
    {
        RowFor(HookTouchPlugin, "Acme.Private.dll")
            .Should()
            .NotBeNull("the hook resolved that assembly, so the plugin shipped it and declared it");

        PluginLogCapture
            .Member(RowFor(HookTouchPlugin, "Acme.Private.dll")!, "LoadState")
            .Should()
            .Be(
                "Loaded",
                "the flag is read where the event is emitted, and the hook forced this assembly's "
                    + "first load after the loader had already finished"
            );
    }

    [Test]
    public void It_reported_a_declared_dependency_no_code_path_touched()
    {
        LogEventPropertyValue? row = RowFor(HookTouchPlugin, "Acme.FixtureContracts.dll");

        row.Should().NotBeNull("an inventory built from the load context's assembly list would omit it");
        PluginLogCapture.Member(row!, "LoadState").Should().Be("NotLoaded");
        PluginLogCapture
            .Member(row!, "Sha256")
            .Should()
            .NotBeNullOrEmpty("a declared file that is present is hashed whether or not it was loaded");
    }

    [Test]
    public void It_reported_the_entry_assembly_version_from_the_loaded_assembly()
    {
        LogEventPropertyValue row = RowFor(HookTouchPlugin, $"{HookTouchPlugin}.dll")!;

        PluginLogCapture
            .Member(row, "EffectiveVersionSource")
            .Should()
            .Be(
                "LoadedAssembly",
                "a framework-dependent publish declares no version for the project's own entry, so "
                    + "reporting a declared one would be reporting a version nobody wrote"
            );
        PluginLogCapture.Member(row, "EffectiveVersion").Should().NotBeNullOrEmpty();

        // Present and null rather than absent, which is the distinction that stops a later reader
        // treating the missing declaration as 0.0.0.0 or dropping the row for want of a version.
        PluginLogCapture.MemberNames(row).Should().Contain("DeclaredAssemblyVersion");
        PluginLogCapture.Member(row, "DeclaredAssemblyVersion").Should().BeNull();
    }

    [Test]
    public void It_named_the_service_types_each_plugin_registered()
    {
        PluginLogCapture
            .Sequence(InventoryFor(HookTouchPlugin), "RegisteredServiceTypes")
            .Select(entry => PluginLogCapture.Member(entry, "ServiceType"))
            .Should()
            .Contain("EdFi.DataManagementService.CustomValidation.ICustomResourceValidator");
    }

    [Test]
    public void It_named_the_implementation_behind_each_registered_service_type()
    {
        // The service type is what every host guard refuses against, and the implementation class is
        // what those guards name. Two plugins registering the same contract are told apart by this
        // member and by nothing else on the event.
        LogEventPropertyValue registration = PluginLogCapture
            .Sequence(InventoryFor(HookTouchPlugin), "RegisteredServiceTypes")
            .Single(entry =>
                PluginLogCapture.Member(entry, "ServiceType")
                == "EdFi.DataManagementService.CustomValidation.ICustomResourceValidator"
            );

        PluginLogCapture
            .Member(registration, "ImplementationType")
            .Should()
            .Be("Acme.DmsHookTouch.HookTouchResourceValidator");
        PluginLogCapture.Member(registration, "Lifetime").Should().Be("Transient");
        PluginLogCapture.Member(registration, "IsKeyed").Should().Be("False");
    }

    [Test]
    public void It_logged_no_configuration_key_or_value_on_the_inventory_event()
    {
        foreach (LogEvent inventory in _capture.InventoryEvents)
        {
            string rendered = inventory.RenderMessage();

            rendered.Should().NotContain(SecretProbeValue);
            rendered.Should().NotContain("test-cms-secret");
            rendered.Should().NotContain("ConfigurationServiceSettings");
            rendered.Should().NotContain("AppSettings:");
        }
    }

    [Test]
    public void It_showed_the_hook_the_registrations_AddServices_had_completed()
    {
        _observedServiceTypes
            .Should()
            .Contain("EdFi.DataManagementService.Core.Security.IClaimSetProvider")
            .And.Contain("EdFi.DataManagementService.Core.Startup.IDmsStartupTask")
            .And.Contain("EdFi.DataManagementService.Core.Configuration.IDataStoreProvider");
    }

    [Test]
    public void It_did_not_show_the_hook_registrations_made_after_AddServices_returned()
    {
        // A delta rather than a categorical claim about a service type. CORS is registered after
        // AddServices returns and before the container is built, so the container has it and the
        // collection the hook was handed did not.
        _observedServiceTypes.Should().NotContain(typeof(ICorsService).FullName);
        _factory!
            .Services.GetService<ICorsService>()
            .Should()
            .NotBeNull("the host registers CORS after AddServices returns, inside the same phase");
    }

    [Test]
    public void It_warned_once_about_the_directory_nobody_allowlisted()
    {
        string[] warnings =
        [
            .. _loaderDiagnostics
                .Split(
                    Environment.NewLine,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Where(line => line.StartsWith("plugins: ignoring directories", StringComparison.Ordinal)),
        ];

        warnings.Should().ContainSingle();
        warnings[0].Should().Contain(UnallowlistedDirectory);
    }

    [Test]
    public void It_replayed_that_warning_through_the_configured_application_logger()
    {
        // The loader's own channel is Console.Error, because it runs before any logging pipeline
        // exists. A deployment collecting application logs rather than container stdout sees nothing
        // written there, so the warning has to arrive through the real logger as well.
        LogEvent replayed = _capture
            .Events.Where(logEvent =>
                logEvent.Level == LogEventLevel.Warning
                && logEvent.MessageTemplate.Text.StartsWith(
                    "Plugin loader warning ",
                    StringComparison.Ordinal
                )
            )
            .Should()
            .ContainSingle()
            .Subject;

        PluginLogCapture
            .ScalarText(replayed, "PluginLoadWarningKind")
            .Should()
            .Be("UnallowlistedDirectories");
        PluginLogCapture
            .Sequence(replayed, "IgnoredDirectories")
            .Select(value => (value as ScalarValue)?.Value?.ToString())
            .Should()
            .BeEquivalentTo(UnallowlistedDirectory);
    }

    [Test]
    public void It_reports_a_declared_version_apart_from_the_version_the_process_has()
    {
        LogEventPropertyValue row = RowFor(HookTouchPlugin, "Acme.Private.dll")!;

        PluginLogCapture.Member(row, "DeclaredAssemblyVersion").Should().Be("1.0.0.0");
        PluginLogCapture.Member(row, "EffectiveVersion").Should().Be("1.0.0.0");
        PluginLogCapture
            .Member(row, "EffectiveVersionSource")
            .Should()
            .Be(
                "DepsJsonDeclaration",
                "this row has a declaration, so the version reported is the one the manifest wrote "
                    + "rather than one read back from the loaded assembly"
            );
        PluginLogCapture.Member(row, "Availability").Should().Be("Present");
        PluginLogCapture.Member(row, "Kind").Should().Be("Managed");
    }

    [Test]
    public void It_reports_the_digest_of_each_file_as_it_sits_in_the_plugin_directory()
    {
        string[] fileNames = [$"{HookTouchPlugin}.dll", "Acme.Private.dll"];

        foreach (string fileName in fileNames)
        {
            PluginLogCapture
                .Member(RowFor(HookTouchPlugin, fileName)!, "Sha256")
                .Should()
                .Be(
                    DigestOf(Path.Combine(_pluginRoot!, HookTouchPlugin, fileName)),
                    "the inventory is what matches a running process to a published artifact, so the "
                        + "digest has to be the one an operator computes over the same bytes"
                );
        }
    }

    [Test]
    public void It_named_the_descriptor_the_plugin_removed_and_the_implementation_it_displaced()
    {
        IReadOnlyList<LogEventPropertyValue> removals = PluginLogCapture.Sequence(
            InventoryFor(HookTouchPlugin),
            "RemovedDescriptors"
        );

        removals.Should().ContainSingle();
        PluginLogCapture
            .Member(removals[0], "ServiceType")
            .Should()
            .Be("Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter");
        PluginLogCapture
            .Member(removals[0], "DisplacedImplementationType")
            .Should()
            .Be(
                "Microsoft.Extensions.Http.LoggingHttpMessageHandlerBuilderFilter",
                "removing a pre-existing descriptor outside the host-owned and logging-pipeline sets "
                    + "is permitted, and this event is the only trace it leaves"
            );
        PluginLogCapture
            .Member(removals[0], "Replaced")
            .Should()
            .Be(
                "False",
                "the plugin took the service type out of service and put nothing back, which is a "
                    + "different fact from a plugin swapping its own descriptor for another"
            );
    }

    [Test]
    public void It_records_no_removal_for_the_plugin_that_removed_nothing()
    {
        PluginLogCapture.Sequence(InventoryFor(ContributorPlugin), "RemovedDescriptors").Should().BeEmpty();
    }

    [Test]
    public void It_records_no_substitution_when_the_host_carries_the_versions_the_plugin_declared()
    {
        // Both fixtures are built against the same assemblies the host runs, so host-first served
        // versions identical to the ones their manifests declare and there is nothing to report. A
        // substitution row with differing versions is exercised where a fixture can be built to skew
        // deliberately, in the plugin hosting unit suite.
        foreach (LogEvent inventory in _capture.InventoryEvents)
        {
            PluginLogCapture.Sequence(inventory, "HostFirstSubstitutions").Should().BeEmpty();
        }
    }

    private static string DigestOf(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private LogEvent InventoryFor(string pluginName) =>
        _capture.InventoryEvents.Single(logEvent =>
            PluginLogCapture.ScalarText(logEvent, "PluginName") == pluginName
        );

    private LogEventPropertyValue? RowFor(string pluginName, string fileName) =>
        PluginLogCapture
            .Sequence(InventoryFor(pluginName), "DeclaredFiles")
            .FirstOrDefault(row =>
                string.Equals(PluginLogCapture.Member(row, "FileName"), fileName, StringComparison.Ordinal)
            );
}
