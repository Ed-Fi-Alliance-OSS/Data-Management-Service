// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// Two plugins each register a custom validator, one of them in a shape the host's own validator audit
/// refuses, and the inventory event attributes the offending implementation to the plugin that
/// supplied it.
/// </summary>
/// <remarks>
/// <para>
/// This is what the ordering and attribution criteria are for. The validator audit runs at startup-task
/// order 250 and names the offending <em>implementation</em> type; it knows nothing about plugins.
/// Without the inventory event ahead of it, an operator is left with a type name and no way back to the
/// plugin that supplied it.
/// </para>
/// <para>
/// Two plugins rather than one, deliberately. Both register the same service type, so both inventory
/// events carry <c>ICustomResourceValidator</c> and the service type alone attributes nothing. Only the
/// implementation type the audit names tells the two apart, and nothing obliges an implementation's
/// namespace to resemble its plugin's name, so the event has to carry it rather than leave it to be
/// guessed.
/// </para>
/// <para>
/// The audit's failure is post-container, so it reaches the process-exit seam rather than escaping
/// host creation. The external doubles already replace that seam with a non-exiting one, which is what
/// keeps the failure inside the test process while both real guards still run.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_APluginRegistersAValidatorTheAuditRefuses
{
    /// <summary>The plugin whose registration the audit accepts.</summary>
    private const string AcceptedPlugin = "Acme.DmsContributor";

    /// <summary>The plugin whose registration the audit refuses.</summary>
    private const string RefusedPlugin = "Acme.DmsHookTouch";

    private const string AcceptedImplementation = "Acme.DmsContributor.FixtureResourceValidator";
    private const string RefusedImplementation = "Acme.DmsHookTouch.HookTouchResourceValidator";
    private const string ContractServiceType =
        "EdFi.DataManagementService.CustomValidation.ICustomResourceValidator";

    /// <summary>
    /// The opening of the message the validator audit throws, as a literal, and the only thing here
    /// that selects the audit's own failure out of everything the boot logged.
    /// </summary>
    /// <remarks>
    /// Matching on the refused implementation's name instead would select the inventory event: the
    /// inventory renders the implementation type of every registration it reports, and it is emitted
    /// first. An assertion written that way holds even if the audit stops naming the offender, which
    /// is the whole thing this case exists to prove.
    /// </remarks>
    private const string AuditFailureMarker = "ICustomResourceValidator registration(s) are invalid";

    private WebApplicationFactory<Program>? _factory;
    private PluginLogCapture _capture = new();
    private string? _pluginRoot;
    private string? _startupStatusFilePath;
    private string? _observationPath;

    [OneTimeSetUp]
    public void Setup()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);

        _pluginRoot = PluginHostProbe.CreatePluginRoot(AcceptedPlugin, RefusedPlugin);
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
            $"{AcceptedPlugin},{RefusedPlugin}",
            _startupStatusFilePath,
            _capture,
            new Dictionary<string, string>
            {
                // Read by both fixtures. Acme.DmsContributor has no case for this value and falls
                // through to its ordinary transient registration, which is what makes one of the two
                // plugins blameless while both register the same contract.
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
    public void It_emitted_an_inventory_event_for_each_plugin()
    {
        _capture
            .InventoryEvents.Select(logEvent => PluginLogCapture.ScalarText(logEvent, "PluginName"))
            .Should()
            .BeEquivalentTo(AcceptedPlugin, RefusedPlugin);
    }

    [Test]
    public void It_emitted_those_events_before_the_validator_audit_reported_the_failure()
    {
        int failureIndex = _capture.IndexOfMessageContaining(AuditFailureMarker);

        failureIndex
            .Should()
            .BeGreaterThanOrEqualTo(0, "the validator audit refuses a non-transient registration");

        foreach (string plugin in new[] { AcceptedPlugin, RefusedPlugin })
        {
            _capture
                .IndexOfInventoryEventFor(plugin)
                .Should()
                .BeInRange(
                    0,
                    failureIndex - 1,
                    "the inventory is emitted immediately after the container is built and before any "
                        + "startup task runs, which is what attributes the offending type to a plugin"
                );
        }
    }

    [Test]
    public void It_named_the_same_service_type_against_both_plugins()
    {
        // The premise of the case rather than an outcome: the service type is common to both events,
        // so on its own it attributes nothing.
        foreach (string plugin in new[] { AcceptedPlugin, RefusedPlugin })
        {
            RegisteredServices(plugin)
                .Select(entry => PluginLogCapture.Member(entry, "ServiceType"))
                .Should()
                .Contain(ContractServiceType);
        }
    }

    [Test]
    public void It_reported_the_implementation_class_the_validator_audit_names()
    {
        LogEvent? auditFailure = _capture.FirstEventContaining(AuditFailureMarker);

        auditFailure.Should().NotBeNull("the audit refuses the non-transient registration");

        string auditText = PluginLogCapture.TextOf(auditFailure!);

        auditText
            .Should()
            .Contain(
                RefusedImplementation,
                "the audit identifies the offending registration by its implementation class, which "
                    + "is the name an operator has to start from"
            );

        auditText
            .Should()
            .NotContain(
                AcceptedImplementation,
                "the other plugin's registration is valid, so naming it here would send an operator "
                    + "to the blameless plugin"
            );
    }

    [Test]
    public void It_attributed_that_implementation_to_the_plugin_that_supplied_it()
    {
        RegisteredServices(RefusedPlugin)
            .Select(entry => PluginLogCapture.Member(entry, "ImplementationType"))
            .Should()
            .Contain(
                RefusedImplementation,
                "the inventory has to carry the same name the audit reports for an operator to reach "
                    + "the plugin from it"
            );

        RegisteredServices(AcceptedPlugin)
            .Select(entry => PluginLogCapture.Member(entry, "ImplementationType"))
            .Should()
            .NotContain(
                RefusedImplementation,
                "the blameless plugin registered the same service type, so attribution that survives "
                    + "must not point at it"
            );
    }

    [Test]
    public void It_carried_the_lifetime_that_made_the_registration_invalid()
    {
        LogEventPropertyValue refused = RegisteredServices(RefusedPlugin)
            .Single(entry => PluginLogCapture.Member(entry, "ImplementationType") == RefusedImplementation);

        PluginLogCapture.Member(refused, "Lifetime").Should().Be("Singleton");
        PluginLogCapture.Member(refused, "IsKeyed").Should().Be("False");

        LogEventPropertyValue accepted = RegisteredServices(AcceptedPlugin)
            .Single(entry => PluginLogCapture.Member(entry, "ImplementationType") == AcceptedImplementation);

        PluginLogCapture
            .Member(accepted, "Lifetime")
            .Should()
            .Be("Transient", "the contract's shape rule is what the refused registration broke");
    }

    private IReadOnlyList<LogEventPropertyValue> RegisteredServices(string pluginName) =>
        PluginLogCapture.Sequence(
            _capture.InventoryEvents.Single(logEvent =>
                PluginLogCapture.ScalarText(logEvent, "PluginName") == pluginName
            ),
            "RegisteredServiceTypes"
        );
}
