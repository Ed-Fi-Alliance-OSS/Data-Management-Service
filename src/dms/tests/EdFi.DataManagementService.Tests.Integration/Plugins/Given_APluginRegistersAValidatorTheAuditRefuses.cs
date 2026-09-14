// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// A plugin registers a custom validator in a shape the host's own validator audit refuses, and the
/// inventory event naming the plugin is already in the log when that audit fails.
/// </summary>
/// <remarks>
/// <para>
/// This is what the ordering criterion is for. The validator audit runs at startup-task order 250 and
/// names the offending implementation type; it knows nothing about plugins. Without the inventory
/// event ahead of it, an operator is left with a type name and no way back to the plugin that
/// supplied it.
/// </para>
/// <para>
/// The audit's failure is post-container, so it reaches the process-exit seam rather than escaping
/// host creation. The external doubles already replace that seam with a non-exiting one, which is what
/// keeps the failure inside the test process while both real guards still run.
/// </para>
/// </remarks>
public sealed class Given_APluginRegistersAValidatorTheAuditRefuses
{
    private const string Plugin = "Acme.DmsHookTouch";

    private WebApplicationFactory<Program>? _factory;
    private PluginLogCapture _capture = new();
    private string? _pluginRoot;
    private string? _startupStatusFilePath;
    private string? _observationPath;

    [OneTimeSetUp]
    public void Setup()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);

        _pluginRoot = PluginHostProbe.CreatePluginRoot(Plugin);
        _startupStatusFilePath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-refused-{Guid.NewGuid():N}.json"
        );
        _observationPath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-refused-touch-{Guid.NewGuid():N}.txt"
        );
        _capture = new PluginLogCapture();

        _factory = PluginHostProbe.CreateHost(
            fixture,
            _pluginRoot,
            Plugin,
            _startupStatusFilePath,
            _capture,
            new Dictionary<string, string>
            {
                ["Fixture:Behavior"] = "validatorWrongLifetime",
                ["Fixture:ObservationPath"] = _observationPath,
            }
        );

        try
        {
            using HttpClient client = _factory.CreateClient();
        }
        catch (Exception)
        {
            // The startup failure is the subject; whether it escapes here or is absorbed by the
            // non-exiting process-exit double is not what this case asserts.
        }
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        PluginHostProbe.DeleteIfPresent(_pluginRoot);
        PluginHostProbe.DeleteIfPresent(_startupStatusFilePath);
        PluginHostProbe.DeleteIfPresent(_observationPath);
    }

    [Test]
    public void It_emitted_the_inventory_event_naming_the_plugin()
    {
        _capture.IndexOfInventoryEventFor(Plugin).Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void It_emitted_that_event_before_the_validator_audit_reported_the_failure()
    {
        int inventoryIndex = _capture.IndexOfInventoryEventFor(Plugin);
        int failureIndex = _capture.IndexOfMessageContaining("ICustomResourceValidator registration");

        failureIndex
            .Should()
            .BeGreaterThanOrEqualTo(0, "the validator audit refuses a non-transient registration");
        inventoryIndex
            .Should()
            .BeLessThan(
                failureIndex,
                "the inventory is emitted immediately after the container is built and before any "
                    + "startup task runs, which is what attributes the offending type to a plugin"
            );
    }

    [Test]
    public void It_named_the_offending_service_type_against_the_plugin()
    {
        PluginLogCapture
            .Sequence(
                _capture.InventoryEvents.Single(logEvent =>
                    PluginLogCapture.ScalarText(logEvent, "PluginName") == Plugin
                ),
                "RegisteredServiceTypes"
            )
            .Select(value => value.ToString().Trim('"'))
            .Should()
            .Contain("EdFi.DataManagementService.CustomValidation.ICustomResourceValidator");
    }
}
