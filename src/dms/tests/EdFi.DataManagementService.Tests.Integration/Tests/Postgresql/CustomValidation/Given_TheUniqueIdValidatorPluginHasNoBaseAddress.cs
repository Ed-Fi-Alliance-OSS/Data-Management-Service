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
/// The same allowlisted plugin, staged the same way, but with no <c>UniqueIdValidation:BaseAddress</c>
/// setting at all.
/// </summary>
/// <remarks>
/// No stub is started here: with the setting entirely absent, the plugin's client factory callback
/// throws on every matching write before it ever reaches a client, so nothing this fixture's cases do
/// could reach a stub even if one were listening.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheUniqueIdValidatorPluginHasNoBaseAddress
    : PostgresqlApiIntegrationTestBase
{
    private string _pluginRoot = string.Empty;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string>
        {
            ["Plugins:Directory"] = _pluginRoot,
            ["Plugins:Allowed"] = UniqueIdValidationPluginScenario.PluginName,
        };

    [OneTimeSetUp]
    public void StageThePlugin() =>
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.CustomValidationFixtureRoot,
            UniqueIdValidationPluginScenario.PluginName
        );

    [OneTimeTearDown]
    public void RemoveThePlugin() => PluginHostProbe.DeleteIfPresent(_pluginRoot);

    [Test]
    public Task It_fails_the_write_when_no_base_address_is_configured() =>
        UniqueIdValidationPluginScenario.It_fails_the_write_when_no_base_address_is_configured(
            Harness
        );

    [Test]
    public Task It_creates_a_non_person_resource_without_a_configured_base_address() =>
        UniqueIdValidationPluginScenario.It_creates_a_non_person_resource_without_a_configured_base_address(
            Harness
        );
}
