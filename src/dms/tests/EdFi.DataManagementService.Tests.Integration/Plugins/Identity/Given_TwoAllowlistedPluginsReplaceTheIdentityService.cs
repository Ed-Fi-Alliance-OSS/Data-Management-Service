// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// Two allowlisted plugins each replace <c>IIdentityService</c>. The host refuses to start,
/// and the failure names both plugins and the contract.
/// </summary>
/// <remarks>
/// The negative control is <c>Given_OnlyOneOfTwoIdentityPluginsIsAllowlisted</c>, which stages the
/// same two plugins and allowlists one.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TwoAllowlistedPluginsReplaceTheIdentityService
{
    private const string FirstPlugin = "Acme.IdentityFixture";
    private const string SecondPlugin = "Acme.SecondIdentityReplacement";
    private const string IdentityServiceType = "EdFi.DataManagementService.Identity.IIdentityService";

    private IdentityPluginHost? _host;
    private Exception? _failure;

    [OneTimeSetUp]
    public void Setup()
    {
        _host = IdentityPluginHost.Create(
            [FirstPlugin, SecondPlugin],
            allowed: $"{FirstPlugin},{SecondPlugin}"
        );
        _failure = _host.TryBoot();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    [Test]
    public void It_refused_to_start()
    {
        _failure.Should().NotBeNull();
    }

    [Test]
    public void It_recorded_a_failed_state()
    {
        PluginHostProbe.ReadStartupStatus(_host!.StartupStatusFilePath)["State"]!
            .GetValue<string>()
            .Should()
            .Be("Failed");
    }

    [Test]
    public void It_recorded_the_failure_under_the_plugin_registration_validation_phase()
    {
        PluginHostProbe.ReadStartupStatus(_host!.StartupStatusFilePath)["Phase"]!
            .GetValue<string>()
            .Should()
            .Be("ValidatePluginRegistrations");
    }

    [Test]
    public void It_named_both_plugins_in_the_status_file_error()
    {
        string message = PluginHostProbe.ReadStartupStatus(_host!.StartupStatusFilePath)[
            "ErrorMessage"
        ]!.GetValue<string>();

        message.Should().Contain(FirstPlugin).And.Contain(SecondPlugin);
    }

    [Test]
    public void It_named_the_identity_contract_in_the_status_file_error()
    {
        PluginHostProbe.ReadStartupStatus(_host!.StartupStatusFilePath)["ErrorMessage"]!
            .GetValue<string>()
            .Should()
            .Contain(IdentityServiceType);
    }

    [Test]
    public void It_logged_a_critical_event_naming_both_plugins_and_the_contract()
    {
        string[] critical =
        [
            .. _host!
                .Capture.Events.Where(e => e.Level == LogEventLevel.Fatal)
                .Select(PluginLogCapture.TextOf),
        ];

        critical
            .Should()
            .Contain(text =>
                text.Contains(FirstPlugin, StringComparison.Ordinal)
                && text.Contains(SecondPlugin, StringComparison.Ordinal)
                && text.Contains(IdentityServiceType, StringComparison.Ordinal)
            );
    }
}
