// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.CustomValidation;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// The shipped default: an empty allowlist over a root holding a directory the loader must never
/// open.
/// </summary>
/// <remarks>
/// Both halves are asserted against a host that reached <c>Ready</c>. An absent inventory event on a
/// boot that failed would prove nothing at all, so the readiness assertion is what gives the absence
/// below its meaning. The unallowlisted directory's entry assembly is deliberately unloadable, which
/// is what makes "never opened" observable rather than assumed.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_NoPluginIsAllowlisted
{
    private WebApplicationFactory<Program>? _factory;
    private PluginLogCapture _capture = new();
    private string? _pluginRoot;
    private string? _startupStatusFilePath;
    private HttpResponseMessage? _metadataResponse;

    [OneTimeSetUp]
    public async Task Setup()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);

        _pluginRoot = PluginHostProbe.CreatePluginRoot();
        PluginHostProbe.WriteCorruptPluginDirectory(_pluginRoot, "Acme.NotAllowlisted");

        _startupStatusFilePath = Path.Combine(
            Path.GetTempPath(),
            $"plugin-integration-empty-{Guid.NewGuid():N}.json"
        );
        _capture = new PluginLogCapture();

        _factory = PluginHostProbe.CreateHost(
            fixture,
            _pluginRoot,
            allowed: string.Empty,
            _startupStatusFilePath,
            _capture
        );

        using HttpClient client = _factory.CreateClient();
        _metadataResponse = await client.GetAsync("/metadata");
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
    }

    [Test]
    public void It_boots_and_serves_requests()
    {
        _metadataResponse!.StatusCode.Should().Be(HttpStatusCode.OK);
        PluginHostProbe
            .ReadStartupStatus(_startupStatusFilePath!)["State"]
            ?.GetValue<string>()
            .Should()
            .Be("Ready");
    }

    [Test]
    public void It_never_opened_the_unallowlisted_directory()
    {
        // Its entry assembly is not an assembly. A boot that read it would have failed, so reaching
        // Ready above is the evidence that it was not read.
        _capture.InventoryEvents.Should().BeEmpty();
    }

    [Test]
    public void It_still_registered_the_audit_input_and_its_startup_task()
    {
        _factory!.Services.GetRequiredService<PluginAuditInput>().Records.Should().BeEmpty();
    }

    [Test]
    public void It_registers_no_custom_validator()
    {
        using IServiceScope scope = _factory!.Services.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IEnumerable<ICustomResourceValidator>>()
            .Should()
            .BeEmpty("nothing was allowlisted, so nothing contributed one");
    }
}
