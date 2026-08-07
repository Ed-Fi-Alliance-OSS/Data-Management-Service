// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// NamespaceBased ProblemDetails at the public HTTP boundary against SQL Server, for stored-value denials and
/// the DELETE authorization-before-precondition ordering.
/// </summary>
public sealed class Given_Mssql_NamespaceAuthorizationProblemDetails_For_Read_And_Delete
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateReadUpdateDeleteClaimSetProvider(fixture);

    [Test]
    public Task It_returns_namespace_mismatch_problem_details_for_get_by_id() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_namespace_mismatch_problem_details_for_get_by_id(
            Harness
        );

    [Test]
    public Task It_returns_stored_uninitialized_problem_details_for_get_by_id() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_stored_uninitialized_problem_details_for_get_by_id(
            Harness
        );

    [Test]
    public Task It_returns_403_rather_than_412_for_an_unauthorized_delete_with_a_stale_if_match() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_403_rather_than_412_for_an_unauthorized_delete_with_a_stale_if_match(
            Harness
        );

    [Test]
    public Task It_returns_412_for_a_stale_delete_if_match_once_namespace_authorization_passes() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_412_for_a_stale_delete_if_match_once_namespace_authorization_passes(
            Harness
        );
}

/// <summary>
/// NamespaceBased ProblemDetails for proposed values on POST create, where the omitted-namespace case proves a
/// missing proposed value reaches authorization through the real pipeline rather than being intercepted by JSON
/// schema validation.
/// </summary>
public sealed class Given_Mssql_NamespaceAuthorizationProblemDetails_For_Create : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateCreateClaimSetProvider(fixture);

    [Test]
    public Task It_returns_proposed_namespace_required_problem_details_for_post_create() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_proposed_namespace_required_problem_details_for_post_create(
            Harness
        );

    [Test]
    public Task It_returns_proposed_namespace_mismatch_problem_details_for_post_create() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_proposed_namespace_mismatch_problem_details_for_post_create(
            Harness
        );
}

/// <summary>
/// The sanitized 500 a malformed AUTH1 payload must produce at the wire boundary. The request raises a genuine
/// SQL Server exception through the production authorization SQL; only the extracted payload is rewritten, by
/// the opt-in test seam.
/// </summary>
public sealed class Given_Mssql_NamespaceAuthorizationProblemDetails_For_An_Unmappable_Auth1_Payload
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateReadUpdateDeleteClaimSetProvider(fixture);

    protected override Func<
        RelationshipAuthorizationProviderFailure,
        RelationshipAuthorizationProviderFailure
    >? ProviderFailureTransform => NamespaceAuthorizationProblemDetailsScenario.ToUnmappablePayload;

    [Test]
    public Task It_returns_a_sanitized_security_configuration_500_for_an_unmappable_payload() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_a_sanitized_security_configuration_500_for_an_unmappable_payload(
            Harness
        );
}

/// <summary>
/// The same sanitized 500 for a payload that cannot be parsed at all, rather than one that parses but cannot be
/// mapped onto the plan. A separate class is required because one provider-failure transform is configured for
/// the lifetime of an API integration fixture.
/// </summary>
public sealed class Given_Mssql_NamespaceAuthorizationProblemDetails_For_A_Malformed_Auth1_Payload
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateReadUpdateDeleteClaimSetProvider(fixture);

    protected override Func<
        RelationshipAuthorizationProviderFailure,
        RelationshipAuthorizationProviderFailure
    >? ProviderFailureTransform => NamespaceAuthorizationProblemDetailsScenario.ToMalformedPayload;

    [Test]
    public Task It_returns_a_sanitized_security_configuration_500_for_a_malformed_payload() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_a_sanitized_security_configuration_500_for_a_malformed_payload(
            Harness
        );
}
