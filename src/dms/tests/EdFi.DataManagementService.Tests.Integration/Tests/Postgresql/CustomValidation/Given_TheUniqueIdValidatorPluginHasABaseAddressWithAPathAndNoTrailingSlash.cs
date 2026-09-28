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
/// The same allowlisted plugin, staged the same way, but configured with a <c>BaseAddress</c> that
/// carries a path and no trailing slash.
/// </summary>
/// <remarks>
/// A sibling fixture rather than another case in <see cref="Given_TheUniqueIdValidatorPluginIsAllowlisted"/>,
/// for the same reason as <see cref="Given_TheUniqueIdValidatorPluginCannotReachItsUpstream"/>:
/// <c>UniqueIdValidation:BaseAddress</c> is read once, while the host boots. The stub here is started
/// with the path prefix <c>uid</c>, so its own address already carries a path segment, and
/// <see cref="UniqueIdServiceStub.BaseAddressWithoutTrailingSlash"/> is what is configured, the way an
/// operator who forgot the trailing slash would write it.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheUniqueIdValidatorPluginHasABaseAddressWithAPathAndNoTrailingSlash
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
            ["UniqueIdValidation:BaseAddress"] = _stub.BaseAddressWithoutTrailingSlash,
        };

    /// <summary>
    /// OneTimeSetUp rather than SetUp, for the same reason the allowlisted fixture uses it: the base
    /// class reads <see cref="AdditionalHostSettings"/> while booting the host in its own per-test
    /// SetUp, and a derived SetUp runs after that one.
    /// </summary>
    [OneTimeSetUp]
    public async Task StageThePluginAndStartTheStub()
    {
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.CustomValidationFixtureRoot,
            UniqueIdValidationPluginScenario.PluginName
        );
        _stub = await UniqueIdServiceStub.StartAsync("uid");
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
    public Task It_creates_a_student_when_the_base_address_has_a_path_and_no_trailing_slash() =>
        UniqueIdValidationPluginScenario.It_creates_a_student_when_the_base_address_has_a_path_and_no_trailing_slash(
            Harness,
            _stub
        );
}
