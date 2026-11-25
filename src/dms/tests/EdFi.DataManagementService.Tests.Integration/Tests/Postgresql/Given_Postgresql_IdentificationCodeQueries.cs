// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// GET-many and partitions filters over identification-code collections on PostgreSQL.
/// </summary>
public sealed class Given_Postgresql_IdentificationCodeQueries : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    // The change-version window bound is read from the published available-change-versions endpoint.
    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new AllowAllClaimSetProvider(fixture, grantReadChanges: true);

    [SetUp]
    public Task Seed() => IdentificationCodeQueryScenario.SeedAsync(Harness);

    [Test]
    public Task It_filters_schools_on_one_collection_element() =>
        IdentificationCodeQueryScenario.It_filters_schools_on_one_collection_element(Harness);

    [Test]
    public Task It_returns_a_school_once_when_several_elements_match() =>
        IdentificationCodeQueryScenario.It_returns_a_school_once_when_several_elements_match(Harness);

    [Test]
    public Task It_filters_staffs_by_assigning_organization_alone_and_with_a_code() =>
        IdentificationCodeQueryScenario.It_filters_staffs_by_assigning_organization_alone_and_with_a_code(
            Harness
        );

    [Test]
    public Task It_filters_student_education_organization_associations_by_code_and_system() =>
        IdentificationCodeQueryScenario.It_filters_student_education_organization_associations_by_code_and_system(
            Harness
        );

    [Test]
    public Task It_returns_an_empty_page_for_an_unresolved_descriptor_uri() =>
        IdentificationCodeQueryScenario.It_returns_an_empty_page_for_an_unresolved_descriptor_uri(Harness);

    [Test]
    public Task It_returns_an_empty_page_for_a_descriptor_value_without_a_fragment() =>
        IdentificationCodeQueryScenario.It_returns_an_empty_page_for_a_descriptor_value_without_a_fragment(
            Harness
        );

    [Test]
    public Task It_rejects_a_descriptor_value_containing_nul() =>
        IdentificationCodeQueryScenario.It_rejects_a_descriptor_value_containing_nul(Harness);

    [Test]
    public Task It_keeps_the_root_meaning_of_the_course_transcript_collision_names() =>
        IdentificationCodeQueryScenario.It_keeps_the_root_meaning_of_the_course_transcript_collision_names(
            Harness
        );

    [Test]
    public Task It_walks_only_the_filtered_set_with_page_tokens() =>
        IdentificationCodeQueryScenario.It_walks_only_the_filtered_set_with_page_tokens(Harness);

    [Test]
    public Task It_partitions_only_the_filtered_set() =>
        IdentificationCodeQueryScenario.It_partitions_only_the_filtered_set(Harness);

    [Test]
    public Task It_combines_a_change_version_window_with_the_filter() =>
        IdentificationCodeQueryScenario.It_combines_a_change_version_window_with_the_filter(Harness);

    [Test]
    public Task It_warns_only_where_no_collection_qualifies() =>
        IdentificationCodeQueryScenario.It_warns_only_where_no_collection_qualifies(Harness);

    [Test]
    public Task It_serves_metadata_publishing_the_accepted_parameters() =>
        IdentificationCodeQueryScenario.It_serves_metadata_publishing_the_accepted_parameters(Harness);
}

/// <summary>
/// Identification-code filters through a profile that hides the collection, on PostgreSQL.
/// </summary>
public sealed class Given_Postgresql_IdentificationCodeQueries_With_A_Profile
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyList<string> AssignedProfileNames =>
        [IdentificationCodeQueryScenario.ProfileName];

    [SetUp]
    public Task Seed() => IdentificationCodeQueryScenario.SeedSchoolsAsync(Harness);

    [Test]
    public Task It_filters_through_a_profile_that_hides_the_collection() =>
        IdentificationCodeQueryScenario.It_filters_through_a_profile_that_hides_the_collection(Harness);

    [Test]
    public Task It_serves_the_parameters_in_the_profile_document() =>
        IdentificationCodeQueryScenario.It_serves_the_parameters_in_the_profile_document(Harness);
}

/// <summary>
/// An identification-code filtered page served from the document cache, on PostgreSQL.
/// </summary>
[Category("DocumentCacheReadAcceleration")]
public sealed class Given_Postgresql_IdentificationCodeQueries_From_The_DocumentCache
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override bool RecordDocumentCacheReadTelemetry => true;

    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:05";

    [SetUp]
    public Task Seed() => IdentificationCodeQueryScenario.SeedCacheResidentSchoolsAsync(Harness);

    [Test]
    public Task It_serves_a_filtered_page_from_the_cache() =>
        IdentificationCodeQueryScenario.It_serves_a_filtered_page_from_the_cache(Harness);
}
