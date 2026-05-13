// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_ProfileRootOnlyMerge_ProfiledHttp : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    [Test]
    public Task It_creates_and_reads_via_visible_profile() =>
        ProfileRootOnlyMergeProfileScenario.It_creates_and_reads_via_visible_profile(Harness);

    [Test]
    public Task It_preserves_hidden_field_on_profiled_put() =>
        ProfileRootOnlyMergeProfileScenario.It_preserves_hidden_field_on_profiled_put(Harness);

    [Test]
    public Task It_rejects_write_against_read_only_profile() =>
        ProfileRootOnlyMergeProfileScenario.It_rejects_write_against_read_only_profile(Harness);
}
