// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_PostActionAuthorization : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        PostActionAuthorizationScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PostActionAuthorizationScenario.CreateClaimSetProvider(fixture);

    [Test]
    public Task It_creates_but_refuses_every_update_for_a_create_only_client() =>
        PostActionAuthorizationScenario.It_creates_but_refuses_every_update_for_a_create_only_client(Harness);

    [Test]
    public Task It_refuses_a_create_but_updates_for_an_update_only_client() =>
        PostActionAuthorizationScenario.It_refuses_a_create_but_updates_for_an_update_only_client(Harness);

    [Test]
    public Task It_refuses_a_post_neither_action_permits_with_the_create_denial() =>
        PostActionAuthorizationScenario.It_refuses_a_post_neither_action_permits_with_the_create_denial(
            Harness
        );

    [Test]
    public Task It_answers_an_update_granted_without_strategies_only_for_an_existing_target() =>
        PostActionAuthorizationScenario.It_answers_an_update_granted_without_strategies_only_for_an_existing_target(
            Harness
        );

    [Test]
    public Task It_applies_the_update_namespace_check_only_to_an_existing_descriptor() =>
        PostActionAuthorizationScenario.It_applies_the_update_namespace_check_only_to_an_existing_descriptor(
            Harness
        );

    [Test]
    public Task It_applies_the_create_namespace_check_only_to_a_new_descriptor() =>
        PostActionAuthorizationScenario.It_applies_the_create_namespace_check_only_to_a_new_descriptor(
            Harness
        );

    [Test]
    public Task It_creates_but_refuses_updates_to_a_descriptor_for_a_create_only_client() =>
        PostActionAuthorizationScenario.It_creates_but_refuses_updates_to_a_descriptor_for_a_create_only_client(
            Harness
        );

    [Test]
    public Task It_refuses_a_descriptor_create_but_updates_for_an_update_only_client() =>
        PostActionAuthorizationScenario.It_refuses_a_descriptor_create_but_updates_for_an_update_only_client(
            Harness
        );

    [Test]
    public Task It_keeps_put_on_the_update_action() =>
        PostActionAuthorizationScenario.It_keeps_put_on_the_update_action(Harness);

    [Test]
    public Task It_keeps_the_put_stored_namespace_check_on_the_update_action() =>
        PostActionAuthorizationScenario.It_keeps_the_put_stored_namespace_check_on_the_update_action(Harness);
}
