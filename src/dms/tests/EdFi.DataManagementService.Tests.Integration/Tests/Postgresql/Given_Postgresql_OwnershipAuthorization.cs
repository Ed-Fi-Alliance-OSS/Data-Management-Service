// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// OwnershipBased authorization at the public HTTP boundary against PostgreSQL: stamping, the authorized round
/// trip, the two denial ProblemDetails contracts, the token cap, and the withheld scopes. The real
/// authorization middleware runs, and the caller's ownership tokens arrive through the production
/// application-context provider rather than being injected into the backend.
/// </summary>
public sealed class Given_Postgresql_OwnershipAuthorization : PostgresqlApiIntegrationTestBase
{
    private readonly RecordingConfigurationServiceApplicationProvider _applicationContextProvider = new(
        OwnershipAuthorizationIntegrationScenario.Resolve
    );

    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    /// <summary>
    /// The tenant route qualifier is how one host serves several ownership token sets: the replaced CMS-facing
    /// provider resolves a different set per tenant, so an owner and a non-owner can act on the same row inside
    /// one test.
    /// </summary>
    protected override bool MultiTenancy => true;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        OwnershipAuthorizationIntegrationScenario.CreateClaimSetProvider(fixture);

    protected override IConfigurationServiceApplicationProvider? ApplicationContextConfigurationProviderOverride =>
        _applicationContextProvider;

    [Test]
    public Task It_stamps_the_creator_ownership_token_on_create_and_never_denies_it() =>
        OwnershipAuthorizationIntegrationScenario.It_stamps_the_creator_ownership_token_on_create_and_never_denies_it(
            Harness
        );

    [Test]
    public Task It_authorizes_the_full_round_trip_for_a_holder_of_the_stored_token() =>
        OwnershipAuthorizationIntegrationScenario.It_authorizes_the_full_round_trip_for_a_holder_of_the_stored_token(
            Harness
        );

    [Test]
    public Task It_returns_ownership_mismatch_problem_details_for_reads_and_writes() =>
        OwnershipAuthorizationIntegrationScenario.It_returns_ownership_mismatch_problem_details_for_reads_and_writes(
            Harness
        );

    [Test]
    public Task It_returns_stored_uninitialized_problem_details_for_reads_and_writes() =>
        OwnershipAuthorizationIntegrationScenario.It_returns_stored_uninitialized_problem_details_for_reads_and_writes(
            Harness
        );

    [Test]
    public Task It_never_discloses_an_ownership_token_value() =>
        OwnershipAuthorizationIntegrationScenario.It_never_discloses_an_ownership_token_value(Harness);

    [Test]
    public Task It_fails_closed_at_the_ownership_token_cap_and_authorizes_just_under_it() =>
        OwnershipAuthorizationIntegrationScenario.It_fails_closed_at_the_ownership_token_cap_and_authorizes_just_under_it(
            Harness
        );

    [Test]
    public Task It_withholds_get_many_from_ownership_with_a_501() =>
        OwnershipAuthorizationIntegrationScenario.It_withholds_get_many_from_ownership_with_a_501(Harness);

    [Test]
    public Task It_withholds_descriptor_operations_from_ownership_with_a_501() =>
        OwnershipAuthorizationIntegrationScenario.It_withholds_descriptor_operations_from_ownership_with_a_501(
            Harness
        );
}
