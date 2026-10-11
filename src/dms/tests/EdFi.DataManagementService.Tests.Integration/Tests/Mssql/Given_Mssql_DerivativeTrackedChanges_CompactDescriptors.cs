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

public sealed class Given_Mssql_DerivativeTrackedChanges_CompactDescriptors : MssqlApiIntegrationTestBase
{
    private readonly MutableNamespacePrefixJwtValidationService _identity = new(
        ExternalDoublesConstants.SmokeToken,
        ExternalDoublesConstants.SmokeClientId,
        [],
        ["uri://ed-fi.org/"]
    );

    protected override FixtureKey Fixture => FixtureKey.CompactDescriptorResources;
    protected override bool BypassAuthorization => false;

    protected override MutableNamespacePrefixJwtValidationService CreateJwtValidationService() => _identity;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, action) =>
                action == "ReadChanges"
                && resource.ResourceName
                    is "SchoolTypeDescriptor"
                        or "AcademicSubjectDescriptor"
                        or "CompactDescriptorItem"
                    ? [AuthorizationStrategyNameConstants.NamespaceBased]
                    : [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired],
            grantReadChanges: true
        );

    [SetUp]
    public void SetupNamespacePrefixes() => _identity.SetNamespacePrefixes(["uri://ed-fi.org/"]);

    [Test]
    public Task It_routes_deleted_descriptors_by_qualified_resource_key_without_live_rows() =>
        CompactDescriptorHistoryScenario.It_routes_deleted_descriptors_by_qualified_resource_key_without_live_rows(
            Harness
        );

    [Test]
    public Task It_preserves_provider_component_comparisons_for_descriptor_recreation() =>
        CompactDescriptorHistoryScenario.It_preserves_provider_component_comparisons_for_descriptor_recreation(
            Harness
        );

    [Test]
    public Task It_snapshots_direct_and_copied_descriptor_identities_for_resource_histories() =>
        CompactDescriptorHistoryScenario.It_snapshots_direct_and_copied_descriptor_identities_for_resource_histories(
            Harness
        );

    [Test]
    public Task It_recreates_resources_using_compact_descriptor_identity_joins() =>
        CompactDescriptorHistoryScenario.It_recreates_resources_using_compact_descriptor_identity_joins(
            Harness
        );

    [Test]
    public Task It_keeps_descriptor_component_updates_and_no_ops_out_of_delete_and_key_history() =>
        CompactDescriptorHistoryScenario.It_keeps_descriptor_component_updates_and_no_ops_out_of_delete_and_key_history(
            Harness
        );

    [Test]
    public Task It_authorizes_retained_namespaces_and_old_descriptor_identity_snapshots() =>
        CompactDescriptorHistoryScenario.It_authorizes_retained_namespaces_and_old_descriptor_identity_snapshots(
            Harness,
            _identity
        );
}
