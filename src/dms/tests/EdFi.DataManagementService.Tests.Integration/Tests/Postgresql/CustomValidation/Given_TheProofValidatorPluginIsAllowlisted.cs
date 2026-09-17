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
/// The custom validation proof plugin is enabled the way a real deployment enables one, and the
/// write pipeline is driven over real HTTP against a leased PostgreSQL database.
/// </summary>
/// <remarks>
/// The plugin is a published directory referencing the two contracts as packed nupkgs from a local
/// folder feed, so what these cases exercise is what an outside implementer can actually build
/// rather than what a project with access to DMS internals can build.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheProofValidatorPluginIsAllowlisted : PostgresqlApiIntegrationTestBase
{
    private string _pluginRoot = string.Empty;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string>
        {
            ["Plugins:Directory"] = _pluginRoot,
            ["Plugins:Allowed"] = CustomValidationPluginScenario.PluginName,
        };

    /// <summary>
    /// A plugin root of this class's own, copied from where the build staged the fixture.
    /// </summary>
    /// <remarks>
    /// OneTimeSetUp rather than SetUp: the base class reads <see cref="AdditionalHostSettings"/>
    /// while booting the host in its own per-test SetUp, and a derived SetUp runs after that one,
    /// so a root created there would not exist yet when the loader looked for it. Staging that
    /// produced nothing usable is named here, by the copy itself, which is ahead of the first boot.
    /// </remarks>
    [OneTimeSetUp]
    public void StageThePlugin() =>
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.CustomValidationFixtureRoot,
            CustomValidationPluginScenario.PluginName
        );

    [OneTimeTearDown]
    public void RemoveThePlugin() => PluginHostProbe.DeleteIfPresent(_pluginRoot);

    [Test]
    public Task It_rejects_a_matching_post_on_the_validation_errors_arm() =>
        CustomValidationPluginScenario.It_rejects_a_matching_post_on_the_validation_errors_arm(Harness);

    [Test]
    public Task It_rejects_a_matching_post_on_the_errors_arm() =>
        CustomValidationPluginScenario.It_rejects_a_matching_post_on_the_errors_arm(Harness);

    [Test]
    public Task It_accepts_a_matching_post_that_passes() =>
        CustomValidationPluginScenario.It_accepts_a_matching_post_that_passes(Harness);

    [Test]
    public Task It_rejects_a_matching_put_and_leaves_the_stored_document_intact() =>
        CustomValidationPluginScenario.It_rejects_a_matching_put_and_leaves_the_stored_document_intact(
            Harness
        );

    [Test]
    public Task It_leaves_a_non_matching_resource_alone() =>
        CustomValidationPluginScenario.It_leaves_a_non_matching_resource_alone(Harness);

    [Test]
    public Task It_matches_the_core_schema_validation_400() =>
        CustomValidationPluginScenario.It_matches_the_core_schema_validation_400(Harness);
}
