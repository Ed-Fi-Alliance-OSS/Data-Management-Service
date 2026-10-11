// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_CompactDescriptorRuntime : MssqlApiIntegrationTestBase
{
    private readonly MutableNamespacePrefixJwtValidationService _identity = new(
        ExternalDoublesConstants.SmokeToken,
        ExternalDoublesConstants.SmokeClientId,
        [],
        ["uri://ed-fi.org/"]
    );

    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;
    protected override bool BypassAuthorization => false;

    protected override MutableNamespacePrefixJwtValidationService CreateJwtValidationService() => _identity;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (_, _) => [AuthorizationStrategyNameConstants.NamespaceBased],
            grantReadChanges: true
        );

    [SetUp]
    public void SetupNamespacePrefixes() => _identity.SetNamespacePrefixes(["uri://ed-fi.org/"]);

    [Test]
    public Task It_applies_RI_matching_post_component_and_casing_updates() =>
        DescriptorRuntimeScenario.It_applies_RI_matching_post_component_and_casing_updates(Harness);

    [Test]
    public Task It_accepts_equal_whole_URI_components_on_put() =>
        DescriptorRuntimeScenario.It_accepts_equal_whole_URI_components_on_put(Harness);

    [Test]
    public Task It_preserves_all_stamps_and_change_queries_on_identical_post_and_put() =>
        DescriptorRuntimeScenario.It_preserves_all_stamps_and_change_queries_on_identical_post_and_put(
            Harness
        );

    [Test]
    public Task It_rejects_a_duplicate_whole_URI_without_an_RI_match() =>
        DescriptorRuntimeScenario.It_rejects_a_duplicate_whole_URI_without_an_RI_match(Harness);

    [Test]
    public Task It_isolates_identical_URIs_across_descriptor_types_and_projects() =>
        DescriptorRuntimeScenario.It_isolates_identical_URIs_across_descriptor_types_and_projects(Harness);

    [Test]
    public Task It_rejects_descriptor_identity_changes() =>
        DescriptorRuntimeScenario.It_rejects_descriptor_identity_changes(Harness);

    [Test]
    public Task It_updates_descriptor_non_identity_fields_and_advances_metadata() =>
        DescriptorRuntimeScenario.It_updates_descriptor_non_identity_fields_and_advances_metadata(Harness);

    [Test]
    public Task It_enforces_namespace_authorization_without_stamp_side_effects() =>
        DescriptorRuntimeScenario.It_enforces_namespace_authorization_without_stamp_side_effects(
            Harness,
            _identity
        );
}
