// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server stored-identity rebind at the public HTTP boundary: identity values equal under the identity
/// collation keep the stored casing on POST and PUT, and authorization, no-op detection and DML see the
/// stored values.
/// </summary>
public sealed class Given_Mssql_CaseVariantIdentity : MssqlApiIntegrationTestBase
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

    [Ignore(
        "DMS-1624: a Section key-change PUT with a StudentSectionAssociation referrer fails with FK_StudentSectionAssociation_Section_RefKey on SQL Server."
    )]
    [Test]
    public Task It_cascades_only_the_genuinely_changed_column_of_a_mixed_put() =>
        CaseVariantIdentityScenario.It_cascades_only_the_genuinely_changed_column_of_a_mixed_put(Harness);

    [Test]
    public Task It_cascades_only_the_genuinely_changed_column_of_a_mixed_class_period_put() =>
        CaseVariantIdentityScenario.It_cascades_only_the_genuinely_changed_column_of_a_mixed_class_period_put(
            Harness
        );

    [Test]
    public Task It_merges_a_recased_profiled_item_whose_profile_hides_a_required_member() =>
        CaseVariantIdentityScenario.It_merges_a_recased_profiled_item_whose_profile_hides_a_required_member(
            Harness
        );
}

/// <summary>
/// Case-variant POSTs under split Create/Update grants on ClassPeriod.
/// </summary>
public sealed class Given_Mssql_CaseVariantIdentity_PostActionAuthorization : MssqlApiIntegrationTestBase
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
public sealed class Given_Mssql_CaseVariantIdentity_Ownership : MssqlApiIntegrationTestBase
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
