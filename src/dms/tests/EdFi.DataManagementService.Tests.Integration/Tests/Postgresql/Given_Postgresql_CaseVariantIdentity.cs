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
/// PostgreSQL twin of the SQL Server case-variant identity suite: identity text compares ordinally, so a case
/// variant is a different identity (a second document, an unresolved reference, or a key change).
/// </summary>
public sealed class Given_Postgresql_CaseVariantIdentity : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_resolves_a_case_variant_class_period_post_by_the_identity_collation() =>
        CaseVariantIdentityScenario.It_resolves_a_case_variant_class_period_post_by_the_identity_collation(
            Harness
        );

    [Test]
    public Task It_resolves_a_case_variant_section_reference_post_by_the_identity_collation() =>
        CaseVariantIdentityScenario.It_resolves_a_case_variant_section_reference_post_by_the_identity_collation(
            Harness
        );

    [Test]
    public Task It_fails_closed_on_a_post_matched_only_by_the_collation() =>
        CaseVariantIdentityScenario.It_fails_closed_on_a_post_matched_only_by_the_collation(Harness);

    [Test]
    public Task It_treats_a_casing_only_put_of_an_immutable_identity_by_the_identity_collation() =>
        CaseVariantIdentityScenario.It_treats_a_casing_only_put_of_an_immutable_identity_by_the_identity_collation(
            Harness
        );

    [Test]
    public Task It_treats_a_casing_only_section_put_by_the_identity_collation() =>
        CaseVariantIdentityScenario.It_treats_a_casing_only_section_put_by_the_identity_collation(Harness);

    [Test]
    public Task It_cascades_only_the_genuinely_changed_column_of_a_mixed_put() =>
        CaseVariantIdentityScenario.It_cascades_only_the_genuinely_changed_column_of_a_mixed_put(Harness);
}

/// <summary>
/// Case-variant POSTs under split Create/Update grants on ClassPeriod.
/// </summary>
public sealed class Given_Postgresql_CaseVariantIdentity_PostActionAuthorization
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CaseVariantIdentityScenario.CreateSwitchableClassPeriodGrants(fixture);

    [Test]
    public Task It_authorizes_a_case_variant_post_of_a_create_only_client_by_the_identity_collation() =>
        CaseVariantIdentityScenario.It_authorizes_a_case_variant_post_of_a_create_only_client_by_the_identity_collation(
            Harness
        );

    [Test]
    public Task It_authorizes_a_case_variant_post_of_an_update_only_client_by_the_identity_collation() =>
        CaseVariantIdentityScenario.It_authorizes_a_case_variant_post_of_an_update_only_client_by_the_identity_collation(
            Harness
        );
}

/// <summary>
/// A case-variant POST by a client that cannot reach the stored row under OwnershipBased authorization. The
/// tenant route qualifier selects the owner or the foreign client's ownership tokens.
/// </summary>
public sealed class Given_Postgresql_CaseVariantIdentity_Ownership : PostgresqlApiIntegrationTestBase
{
    private readonly RecordingConfigurationServiceApplicationProvider _applicationContextProvider = new(
        CaseVariantIdentityScenario.ResolveOwnershipTenant
    );

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool BypassAuthorization => false;

    protected override bool MultiTenancy => true;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CaseVariantIdentityScenario.CreateOwnershipClaimSetProvider(fixture);

    protected override IConfigurationServiceApplicationProvider? ApplicationContextConfigurationProviderOverride =>
        _applicationContextProvider;

    [Test]
    public Task It_evaluates_ownership_of_a_case_variant_post_against_the_stored_row() =>
        CaseVariantIdentityScenario.It_evaluates_ownership_of_a_case_variant_post_against_the_stored_row(
            Harness
        );
}
