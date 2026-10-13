// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_AuthoritativeTimestampReadConsistency : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CompactDescriptorResources;
    protected override bool EnableDocumentCacheReadAcceleration => false;

    protected override void CustomizeServices(
        IServiceCollection services,
        FixtureContext fixture,
        string leasedConnectionString
    ) => AuthoritativeTimestampReadConsistencyScenario.Configure(services, leasedConnectionString, true);

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task It_retries_descriptor_candidates_after_leading_versions(bool page, bool restamp) =>
        AuthoritativeTimestampReadConsistencyScenario.RunAsync(
            Harness,
            true,
            page,
            restamp,
            false,
            selectedDescriptorFallback: true,
            beforeMetadata: true
        );

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task It_retries_a_descriptor_commit_after_leading_versions(bool page, bool restamp) =>
        AuthoritativeTimestampReadConsistencyScenario.RunAsync(
            Harness,
            true,
            page,
            restamp,
            false,
            beforeMetadata: true
        );

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task It_retries_a_commit_between_leading_versions_and_owner_metadata(bool page, bool restamp) =>
        AuthoritativeTimestampReadConsistencyScenario.RunAsync(
            Harness,
            false,
            page,
            restamp,
            false,
            beforeMetadata: true
        );

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task It_reads_a_committed_state_after_target_selection(bool page, bool restamp) =>
        AuthoritativeTimestampReadConsistencyScenario.RunAsync(Harness, false, page, restamp, false);

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task It_reads_a_committed_state_across_metadata_and_content(bool page, bool restamp) =>
        AuthoritativeTimestampReadConsistencyScenario.RunAsync(Harness, false, page, restamp, true);

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task It_reads_one_unequal_id_descriptor_state_during_a_commit(bool page, bool restamp) =>
        AuthoritativeTimestampReadConsistencyScenario.RunAsync(Harness, true, page, restamp, false);

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task It_rehydrates_unequal_id_descriptor_candidates_without_cache(bool page, bool restamp) =>
        AuthoritativeTimestampReadConsistencyScenario.RunAsync(Harness, true, page, restamp, false, true);
}
