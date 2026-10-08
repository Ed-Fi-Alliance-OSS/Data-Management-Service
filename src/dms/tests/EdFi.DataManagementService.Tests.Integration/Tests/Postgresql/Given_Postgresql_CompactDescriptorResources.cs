// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_CompactDescriptorResources : PostgresqlApiIntegrationTestBase
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
            static (resource, _) =>
                resource.ResourceName
                    is "CompactDescriptorItem"
                        or "SchoolTypeDescriptor"
                        or "AcademicSubjectDescriptor"
                    ? [AuthorizationStrategyNameConstants.NamespaceBased]
                    : [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired],
            grantReadChanges: true
        );

    [SetUp]
    public void SetupNamespacePrefixes() => _identity.SetNamespacePrefixes(["uri://ed-fi.org/"]);

    protected override void CustomizeServices(
        IServiceCollection services,
        FixtureContext fixture,
        string leasedConnectionString
    ) => CompactDescriptorResourceScenario.RecordWriteCommands(services);

    [Test]
    public Task It_batches_repeated_descriptor_lookups_in_the_existing_write_command_stream() =>
        CompactDescriptorResourceScenario.It_batches_repeated_descriptor_lookups_in_the_existing_write_command_stream(
            Harness
        );

    [Test]
    public Task It_stores_compact_keys_and_hydrates_root_collection_extension_and_copied_URIs() =>
        CompactDescriptorResourceScenario.It_stores_compact_keys_and_hydrates_root_collection_extension_and_copied_URIs(
            Harness
        );

    [Test]
    public Task It_matches_repeated_POST_and_transitive_document_identities_with_mixed_case_descriptors() =>
        CompactDescriptorResourceScenario.It_matches_repeated_POST_and_transitive_document_identities_with_mixed_case_descriptors(
            Harness
        );

    [Test]
    public Task It_filters_whole_descriptor_URIs_on_direct_and_transitive_identity_paths() =>
        CompactDescriptorResourceScenario.It_filters_whole_descriptor_URIs_on_direct_and_transitive_identity_paths(
            Harness
        );

    [Test]
    public Task It_protects_referenced_descriptors_without_changing_rows_or_stamps() =>
        CompactDescriptorResourceScenario.It_protects_referenced_descriptors_without_changing_rows_or_stamps(
            Harness
        );

    [Test]
    public Task It_maintains_RI_and_copied_descriptor_keys_after_resource_identity_updates() =>
        CompactDescriptorResourceScenario.It_maintains_RI_and_copied_descriptor_keys_after_resource_identity_updates(
            Harness
        );

    [Test]
    public Task It_authorizes_stored_and_proposed_namespaces_through_compact_descriptor_keys() =>
        CompactDescriptorResourceScenario.It_authorizes_stored_and_proposed_namespaces_through_compact_descriptor_keys(
            Harness,
            _identity
        );
}
