// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_AuthoritativeResponseMetadata : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CompactDescriptorResources;
    protected override bool CaptureQueryPlans => true;

    protected override void CustomizeServices(
        IServiceCollection services,
        FixtureContext fixture,
        string leasedConnectionString
    ) =>
        AuthoritativeResponseMetadataScenario.RecordHydrationCommands(
            services,
            leasedConnectionString,
            false
        );

    [TestCase(false)]
    [TestCase(true)]
    public Task It_serves_owner_metadata_after_creation_changes_no_ops_and_restamping(bool descriptor) =>
        AuthoritativeResponseMetadataScenario.It_serves_owner_metadata_after_creation_changes_no_ops_and_restamping(
            Harness,
            descriptor
        );

    [TestCase(false)]
    [TestCase(true)]
    public Task It_preserves_wrong_resource_and_conditional_lookup_behavior(bool descriptor) =>
        AuthoritativeResponseMetadataScenario.It_preserves_wrong_resource_and_conditional_lookup_behavior(
            Harness,
            descriptor
        );

    [Test]
    public Task It_keeps_creation_and_last_modified_equal_when_a_post_inserts_child_rows() =>
        AuthoritativeResponseMetadataScenario.It_keeps_creation_and_last_modified_equal_when_a_post_inserts_child_rows(
            Harness
        );

    [TestCase(false)]
    [TestCase(true)]
    public Task It_starts_a_new_creation_time_when_a_deleted_resource_is_recreated(bool descriptor) =>
        AuthoritativeResponseMetadataScenario.It_starts_a_new_creation_time_when_a_deleted_resource_is_recreated(
            Harness,
            descriptor
        );

    [Test]
    public Task It_keeps_page_metadata_in_one_batch_as_the_page_grows() =>
        AuthoritativeResponseMetadataScenario.It_keeps_page_metadata_in_one_batch_as_the_page_grows(Harness);
}
