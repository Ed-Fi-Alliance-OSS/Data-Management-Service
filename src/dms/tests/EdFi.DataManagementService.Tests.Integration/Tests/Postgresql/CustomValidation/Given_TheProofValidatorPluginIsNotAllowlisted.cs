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
/// The negative control: the same staged plugin, under the same plugin root, with its name removed
/// from the allowlist and nothing else changed.
/// </summary>
/// <remarks>
/// <para>
/// This is what proves the validator was reached through the plugin the story added rather than
/// through some other path. Every setting the allowlisted class uses is repeated here verbatim
/// except <c>Plugins:Allowed</c>, and the requests the cases send are byte-identical to the ones
/// that are refused there.
/// </para>
/// <para>
/// Removing the only name leaves the allowlist empty, and an empty allowlist never probes the
/// plugin root at all. That is exactly how a deployment disables a plugin, and the directory is
/// still staged and still present under the root either way, so the difference between the two
/// classes remains the one setting.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheProofValidatorPluginIsNotAllowlisted : PostgresqlApiIntegrationTestBase
{
    private string _pluginRoot = string.Empty;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string>
        {
            ["Plugins:Directory"] = _pluginRoot,
            ["Plugins:Allowed"] = string.Empty,
        };

    [OneTimeSetUp]
    public void StageThePlugin() =>
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.CustomValidationFixtureRoot,
            CustomValidationPluginScenario.PluginName
        );

    [OneTimeTearDown]
    public void RemoveThePlugin() => PluginHostProbe.DeleteIfPresent(_pluginRoot);

    [Test]
    public Task It_accepts_the_failing_post_when_the_plugin_is_not_allowlisted() =>
        CustomValidationPluginScenario.It_accepts_the_failing_post_when_the_plugin_is_not_allowlisted(
            Harness
        );

    [Test]
    public Task It_accepts_the_failing_put_when_the_plugin_is_not_allowlisted() =>
        CustomValidationPluginScenario.It_accepts_the_failing_put_when_the_plugin_is_not_allowlisted(Harness);
}
