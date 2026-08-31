// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

[Category("DocumentCacheCompletedProjection")]
public sealed class Given_Postgresql_DocumentCacheCompletedProjection : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override bool RecordDocumentCacheReadTelemetry => true;

    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:05";

    [Test]
    public Task It_projects_http_created_updated_and_deleted_ordinary_resource() =>
        DocumentCacheCompletedProjectionScenario.It_projects_http_created_updated_and_deleted_ordinary_resource(
            Harness
        );
}
