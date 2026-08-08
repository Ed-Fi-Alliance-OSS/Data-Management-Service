// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

[Category("DocumentCacheReadAcceleration")]
public sealed class Given_Mssql_DocumentCacheReadAcceleration : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    [Test]
    public Task It_serves_cached_get_and_query_for_ordinary_resources() =>
        DocumentCacheReadAccelerationScenario.It_serves_cached_get_and_query_for_ordinary_resources(Harness);

    [Test]
    public Task It_falls_back_relationally_when_cache_row_is_missing_or_stale() =>
        DocumentCacheReadAccelerationScenario.It_falls_back_relationally_when_cache_row_is_missing_or_stale(
            Harness
        );

    [Test]
    public Task It_shapes_cached_profile_and_descriptor_conditional_get() =>
        DocumentCacheReadAccelerationScenario.It_shapes_cached_profile_and_descriptor_conditional_get(
            Harness
        );
}

[Category("DocumentCacheReadAcceleration")]
public sealed class Given_Mssql_DocumentCacheReadAcceleration_With_DescriptorRuntime
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    [Test]
    public Task It_serves_descriptor_query_from_cache_and_falls_back_for_incomplete_pages() =>
        DocumentCacheReadAccelerationScenario.It_serves_descriptor_query_from_cache_and_falls_back_for_incomplete_pages(
            Harness
        );
}

[Category("DocumentCacheReadAcceleration")]
public sealed class Given_Mssql_DocumentCacheReadAcceleration_With_ResourceLinks_Disabled
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override bool ResourceLinksEnabled => false;

    [Test]
    public Task It_strips_links_from_cached_resource_when_resource_links_are_disabled() =>
        DocumentCacheReadAccelerationScenario.It_strips_links_from_cached_resource_when_resource_links_are_disabled(
            Harness
        );
}

[Category("DocumentCacheReadAcceleration")]
public sealed class Given_Mssql_DocumentCacheReadAcceleration_With_Read_Authorization_Denied
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        DocumentCacheReadAccelerationScenario.CreateStudentCreateOnlyClaimSetProvider();

    [Test]
    public Task It_does_not_serve_cached_body_when_read_authorization_is_denied() =>
        DocumentCacheReadAccelerationScenario.It_does_not_serve_cached_body_when_read_authorization_is_denied(
            Harness
        );
}
