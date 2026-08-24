// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// Executes the static ODS 7.3.2 comparison cases against a PostgreSQL-backed host. This binding carries
/// every group except the two that need a differently configured host: the empty-hydration group needs
/// the hydration seam, and the profile group needs the fixture that owns profile XML.
/// </summary>
/// <remarks>
/// Leases the cursor-partition-contract fixture because several cases need the extension resource whose
/// schema declares a query field named <c>number</c>. The maximum page size is lowered so the sizing
/// case is cut by a computed partition size rather than by the minimum, which is the only arrangement in
/// which a true ceiling and a floor produce different boundaries; the published-metadata cases read the
/// same configured value back out of the served document, so the two uses reinforce each other.
/// </remarks>
public sealed class Given_Postgresql_OdsComparison : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    protected override int? MaximumPageSizeOverride => OdsComparisonScenario.HostMaximumPageSize;

    [Test]
    public Task It_matches_the_recorded_ods_outcomes_for_the_validation_cases() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "validation");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_the_omitted_limit_default() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "omitted-limit-default");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_partition_sizing() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "sizing");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_the_number_collision() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "number-collision");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_int64_range_bounds() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "int64-bounds");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_at_the_identity_maximum() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "identity-maximum");

    [Test]
    public Task It_matches_the_recorded_ods_outcomes_for_the_published_metadata() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "metadata");
}

/// <summary>
/// The empty-hydration comparison, which needs a host whose first hydrated page returns no rows while
/// keeping its selected maximum. That seam is a per-fixture opt-in, so it gets its own binding rather
/// than changing every other case's host.
/// </summary>
public sealed class Given_Postgresql_OdsComparisonEmptyHydration : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    protected override int? MaximumPageSizeOverride => OdsComparisonScenario.HostMaximumPageSize;

    protected override bool SuppressHydratedRowsOnce => true;

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_the_empty_hydration_header() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "empty-hydration");
}

/// <summary>
/// The profile comparison, bound to the fixture that owns profile XML.
/// </summary>
/// <remarks>
/// Assigning the write-only profile is what puts a request naming that profile onto the path where the
/// resource is found to have no readable content type.
/// </remarks>
public sealed class Given_Postgresql_OdsComparisonProfile : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyList<string> AssignedProfileNames =>
        [PartitionProfileScenario.AssignedWriteOnlyProfileName];

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_profile_method_usage() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "profile");
}

/// <summary>
/// The profile-document comparison, bound to the authoritative Data Standard 5.2 fixture.
/// </summary>
/// <remarks>
/// It has to be this fixture. The focused fixtures declare empty OpenAPI base-document paths, so their
/// served documents publish no paths at all and the omission under test would hold vacuously. The
/// authoritative document publishes both the collection path and its <c>/partitions</c> sibling, which
/// is what makes the profile document's omission of the second one observable. PostgreSQL only:
/// document assembly and profile filtering answer before a provider is involved.
/// </remarks>
public sealed class Given_Postgresql_OdsComparisonProfileDocument : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyList<string> AssignedProfileNames =>
        [OdsComparisonScenario.ProfileDocumentProfileName];

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_the_write_only_profile_document() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "profile-metadata");
}
