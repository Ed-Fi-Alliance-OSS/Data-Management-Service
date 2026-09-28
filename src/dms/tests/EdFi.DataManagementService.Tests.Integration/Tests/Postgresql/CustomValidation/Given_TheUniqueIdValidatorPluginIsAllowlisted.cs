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
/// The reference UniqueId validator plugin is enabled the way a real deployment enables one, pointed
/// at an in-process stub of the external unique-id system, and the write pipeline is driven over real
/// HTTP against a leased PostgreSQL database.
/// </summary>
/// <remarks>
/// The plugin is a published directory referencing the two contracts as packed nupkgs from a local
/// folder feed, so what these cases exercise is what an outside implementer can actually build rather
/// than what a project with access to DMS internals can build.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheUniqueIdValidatorPluginIsAllowlisted : PostgresqlApiIntegrationTestBase
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
        };

    /// <summary>
    /// A plugin root of this class's own, copied from where the build staged the fixture, and the
    /// stub the plugin's own <c>HttpClient</c> reaches over the loopback address above.
    /// </summary>
    /// <remarks>
    /// OneTimeSetUp rather than SetUp: the base class reads <see cref="AdditionalHostSettings"/> while
    /// booting the host in its own per-test SetUp, and a derived SetUp runs after that one, so a root
    /// or a stub created there would not exist yet when the loader, and the plugin's own
    /// <c>ConfigureHttpClient</c> callback, first looked for them.
    /// </remarks>
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

    /// <summary>
    /// Clears what the previous case taught the stub. The stub itself outlives every test in this
    /// fixture - it is started once, in <c>OneTimeSetUp</c> - so without this reset a later case would
    /// still see an earlier case's known ids and its request log.
    /// </summary>
    [SetUp]
    public void ResetTheStub() => _stub.Reset();

    [Test]
    public Task It_creates_a_student_the_stub_knows() =>
        UniqueIdValidationPluginScenario.It_creates_a_student_the_stub_knows(Harness, _stub);

    [Test]
    public Task It_creates_a_staff_the_stub_knows() =>
        UniqueIdValidationPluginScenario.It_creates_a_staff_the_stub_knows(Harness, _stub);

    [Test]
    public Task It_creates_a_contact_the_stub_knows() =>
        UniqueIdValidationPluginScenario.It_creates_a_contact_the_stub_knows(Harness, _stub);

    [Test]
    public Task It_rejects_a_student_with_an_unknown_unique_id() =>
        UniqueIdValidationPluginScenario.It_rejects_a_student_with_an_unknown_unique_id(Harness);

    [Test]
    public Task It_rejects_a_staff_with_an_unknown_unique_id() =>
        UniqueIdValidationPluginScenario.It_rejects_a_staff_with_an_unknown_unique_id(Harness);

    [Test]
    public Task It_rejects_a_contact_with_an_unknown_unique_id() =>
        UniqueIdValidationPluginScenario.It_rejects_a_contact_with_an_unknown_unique_id(Harness);

    [Test]
    public Task It_rejects_a_repeat_post_after_the_stub_forgets_the_id() =>
        UniqueIdValidationPluginScenario.It_rejects_a_repeat_post_after_the_stub_forgets_the_id(
            Harness,
            _stub
        );

    [Test]
    public Task It_rejects_a_put_after_the_stub_forgets_the_id() =>
        UniqueIdValidationPluginScenario.It_rejects_a_put_after_the_stub_forgets_the_id(
            Harness,
            _stub
        );

    [Test]
    public Task It_creates_a_non_person_resource_without_calling_the_stub() =>
        UniqueIdValidationPluginScenario.It_creates_a_non_person_resource_without_calling_the_stub(
            Harness,
            _stub
        );

    [Test]
    public Task It_rejects_a_key_change_to_an_unknown_unique_id_on_the_data_validation_arm() =>
        UniqueIdValidationPluginScenario.It_rejects_a_key_change_to_an_unknown_unique_id_on_the_data_validation_arm(
            Harness,
            _stub
        );

    [Test]
    public Task It_rejects_a_key_change_to_a_known_unique_id_as_key_change_not_supported() =>
        UniqueIdValidationPluginScenario.It_rejects_a_key_change_to_a_known_unique_id_as_key_change_not_supported(
            Harness,
            _stub
        );

    [Test]
    public Task It_fails_the_write_when_the_stub_answers_server_error() =>
        UniqueIdValidationPluginScenario.It_fails_the_write_when_the_stub_answers_server_error(
            Harness,
            _stub
        );

    [Test]
    public Task It_escapes_a_unique_id_containing_reserved_characters() =>
        UniqueIdValidationPluginScenario.It_escapes_a_unique_id_containing_reserved_characters(
            Harness,
            _stub
        );

    [Test]
    public Task It_fails_the_write_when_the_stub_redirects() =>
        UniqueIdValidationPluginScenario.It_fails_the_write_when_the_stub_redirects(Harness, _stub);
}
