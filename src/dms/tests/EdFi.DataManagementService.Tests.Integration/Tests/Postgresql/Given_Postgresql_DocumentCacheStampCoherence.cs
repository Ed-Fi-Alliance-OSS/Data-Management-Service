// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_DocumentCacheStampCoherence : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CompactDescriptorResources;
    protected override bool EnableDocumentCacheReadAcceleration => true;
    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:30";

    protected override void CustomizeServices(
        IServiceCollection services,
        FixtureContext fixture,
        string leasedConnectionString
    ) => DocumentCacheStampCoherenceScenario.Configure(services, leasedConnectionString, true);

    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public Task It_keeps_projection_and_cached_responses_coherent_during_commits(
        bool descriptor,
        bool restamp,
        bool publication
    ) => DocumentCacheStampCoherenceScenario.RunAsync(Harness, descriptor, restamp, publication);
}
