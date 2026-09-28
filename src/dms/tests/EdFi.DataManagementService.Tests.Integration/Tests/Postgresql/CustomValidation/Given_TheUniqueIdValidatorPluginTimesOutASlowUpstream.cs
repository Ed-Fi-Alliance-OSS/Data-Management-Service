// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Plugins;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql.CustomValidation;

/// <summary>
/// The same allowlisted plugin, staged the same way, but configured with <c>UniqueIdValidation:Timeout</c>
/// set to one second and pointed at a stub that delays every answer by ten.
/// </summary>
/// <remarks>
/// A sibling fixture rather than another case in <see cref="Given_TheUniqueIdValidatorPluginIsAllowlisted"/>,
/// for the same reason as the other siblings: <c>UniqueIdValidation:Timeout</c> comes from the host's
/// configuration and is fixed for that host's lifetime, so a different value needs its own fixture
/// class, and every other case in that fixture relies on the deployed default.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheUniqueIdValidatorPluginTimesOutASlowUpstream
    : PostgresqlApiIntegrationTestBase
{
    private string _pluginRoot = string.Empty;
    private UniqueIdServiceStub _stub = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string>
        {
            ["Plugins:Directory"] = _pluginRoot,
            ["Plugins:Allowed"] = UniqueIdValidationPluginScenario.PluginName,
            ["UniqueIdValidation:BaseAddress"] = _stub.BaseAddress.ToString(),
            ["UniqueIdValidation:Timeout"] = "00:00:01",
        };

    [OneTimeSetUp]
    public async Task StageThePluginAndStartTheStub()
    {
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.CustomValidationFixtureRoot,
            UniqueIdValidationPluginScenario.PluginName
        );
        _stub = await UniqueIdServiceStub.StartAsync();
    }

    [OneTimeTearDown]
    public async Task RemoveThePluginAndStopTheStub()
    {
        PluginHostProbe.DeleteIfPresent(_pluginRoot);
        await _stub.DisposeAsync();
    }

    [SetUp]
    public void ResetTheStub() => _stub.Reset();

    [Test]
    public Task It_fails_the_write_when_the_upstream_times_out() =>
        UniqueIdValidationPluginScenario.It_fails_the_write_when_the_upstream_times_out(
            Harness,
            _stub
        );
}
