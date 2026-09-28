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
/// The same allowlisted plugin, staged the same way, but configured with a <c>BaseAddress</c> nothing
/// listens on.
/// </summary>
/// <remarks>
/// A sibling fixture rather than another case in <see cref="Given_TheUniqueIdValidatorPluginIsAllowlisted"/>,
/// because <c>UniqueIdValidation:BaseAddress</c> is read once, while the host boots, from
/// <see cref="ApiIntegrationTestBase.AdditionalHostSettings"/>; every other case in that fixture needs
/// the address of a stub that is actually listening, and this one needs the opposite.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheUniqueIdValidatorPluginCannotReachItsUpstream
    : PostgresqlApiIntegrationTestBase
{
    private string _pluginRoot = string.Empty;
    private Uri _unreachableBaseAddress = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string>
        {
            ["Plugins:Directory"] = _pluginRoot,
            ["Plugins:Allowed"] = UniqueIdValidationPluginScenario.PluginName,
            ["UniqueIdValidation:BaseAddress"] = _unreachableBaseAddress.ToString(),
        };

    [OneTimeSetUp]
    public void StageThePluginAndChooseADeadAddress()
    {
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.CustomValidationFixtureRoot,
            UniqueIdValidationPluginScenario.PluginName
        );
        _unreachableBaseAddress = UniqueIdServiceStub.UnreachableBaseAddress();
    }

    [OneTimeTearDown]
    public void RemoveThePlugin() => PluginHostProbe.DeleteIfPresent(_pluginRoot);

    [Test]
    public Task It_fails_the_write_when_the_upstream_is_unreachable() =>
        UniqueIdValidationPluginScenario.It_fails_the_write_when_the_upstream_is_unreachable(
            Harness
        );
}
