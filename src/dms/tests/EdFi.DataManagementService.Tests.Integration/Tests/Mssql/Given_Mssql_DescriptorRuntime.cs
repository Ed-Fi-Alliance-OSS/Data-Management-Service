// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_DescriptorRuntime : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    [Test]
    public Task It_creates_and_reads_a_descriptor() =>
        DescriptorRuntimeScenario.It_creates_and_reads_a_descriptor(Harness);

    [Test]
    public Task It_returns_not_modified_for_a_matching_descriptor_if_none_match() =>
        DescriptorRuntimeScenario.It_returns_not_modified_for_a_matching_descriptor_if_none_match(Harness);

    [Test]
    public Task It_updates_descriptor_non_identity_fields_and_advances_metadata() =>
        DescriptorRuntimeScenario.It_updates_descriptor_non_identity_fields_and_advances_metadata(Harness);

    [Test]
    public Task It_preserves_metadata_for_unchanged_descriptor_put() =>
        DescriptorRuntimeScenario.It_preserves_metadata_for_unchanged_descriptor_put(Harness);

    [Test]
    public Task It_rejects_descriptor_identity_changes() =>
        DescriptorRuntimeScenario.It_rejects_descriptor_identity_changes(Harness);

    [Test]
    public Task It_filters_and_pages_descriptor_queries() =>
        DescriptorRuntimeScenario.It_filters_and_pages_descriptor_queries(Harness);

    [Test]
    public Task It_deletes_a_descriptor() => DescriptorRuntimeScenario.It_deletes_a_descriptor(Harness);

    [Test]
    public Task It_rejects_descriptor_delete_when_referenced() =>
        DescriptorRuntimeScenario.It_rejects_descriptor_delete_when_referenced(Harness);

    [Test]
    public Task It_requires_descriptor_reference_resolution_before_resource_write() =>
        DescriptorRuntimeScenario.It_requires_descriptor_reference_resolution_before_resource_write(Harness);
}
