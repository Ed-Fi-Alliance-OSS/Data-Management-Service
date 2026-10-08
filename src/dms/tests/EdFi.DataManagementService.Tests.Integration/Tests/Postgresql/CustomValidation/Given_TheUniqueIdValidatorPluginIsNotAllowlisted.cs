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
/// The negative control: the same staged plugin, under the same plugin root, pointed at a stub with
/// the same <c>BaseAddress</c>, with its name removed from the allowlist and nothing else changed.
/// </summary>
/// <remarks>
/// <para>
/// This is what proves the rejection <see cref="Given_TheUniqueIdValidatorPluginIsAllowlisted"/>
/// asserts is reached through the plugin the story added rather than through some other path. Every
/// setting that class uses is repeated here verbatim except <c>Plugins:Allowed</c>, and the one case
/// here builds its request from the same shared definition that class's unknown-id Student case
/// uses, so the POST serializes the same bytes there and here.
/// </para>
/// <para>
/// Removing the only name leaves the allowlist empty, and an empty allowlist never probes the
/// plugin root at all. That is exactly how a deployment disables a plugin, and the directory is
/// still staged and still present under the root either way, so the difference between the two
/// classes remains the one setting. The stub is started even though nothing ever reaches it,
/// because <c>UniqueIdValidation:BaseAddress</c> comes from the host's configuration and is fixed
/// for that host's lifetime, so this class needs to repeat that setting verbatim rather than leave
/// it out.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheUniqueIdValidatorPluginIsNotAllowlisted : PostgresqlApiIntegrationTestBase
{
    private string _pluginRoot = string.Empty;
    private UniqueIdServiceStub _stub = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string>
        {
            ["Plugins:Directory"] = _pluginRoot,
            ["Plugins:Allowed"] = string.Empty,
            ["UniqueIdValidation:BaseAddress"] = _stub.BaseAddress.ToString(),
        };

    [OneTimeSetUp]
    public async Task StageThePluginAndStartTheStub()
    {
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.PackedContractFixtureRoot,
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

    [Test]
    public Task It_accepts_the_failing_post_when_the_plugin_is_not_allowlisted() =>
        UniqueIdValidationPluginScenario.It_accepts_the_failing_post_when_the_plugin_is_not_allowlisted(
            Harness,
            _stub
        );
}
