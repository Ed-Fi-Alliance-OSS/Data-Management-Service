// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// The negative control for <c>Given_TwoAllowlistedPluginsReplaceTheIdentityService</c>: the same
/// two replacement plugins are staged but only the first is allowlisted, so exactly one plugin claims
/// <c>IIdentityService</c> and the host starts.
/// </summary>
[Category("PluginIntegration")]
public sealed class Given_OnlyOneOfTwoIdentityPluginsIsAllowlisted
{
    private const string FirstPlugin = "Acme.IdentityFixture";
    private const string SecondPlugin = "Acme.SecondIdentityReplacement";

    private IdentityPluginHost? _host;
    private Exception? _failure;

    [OneTimeSetUp]
    public void Setup()
    {
        _host = IdentityPluginHost.Create([FirstPlugin, SecondPlugin], allowed: FirstPlugin);
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
    public void It_started()
    {
        _failure.Should().BeNull();
    }

    [Test]
    public void It_reached_the_ready_phase()
    {
        PluginHostProbe.ReadStartupStatus(_host!.StartupStatusFilePath)["State"]!
            .GetValue<string>()
            .Should()
            .Be("Ready");
    }

    [Test]
    public void It_loaded_only_the_allowlisted_plugin()
    {
        _host!
            .Capture.InventoryEvents.Select(logEvent => PluginLogCapture.ScalarText(logEvent, "PluginName"))
            .Should()
            .BeEquivalentTo(FirstPlugin);
    }

    [Test]
    public void It_logged_no_critical_event()
    {
        _host!.Capture.Events.Where(e => e.Level == LogEventLevel.Fatal).Should().BeEmpty();
    }
}
