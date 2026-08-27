// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// Real-HTTP-pipeline coverage for the DMS-1373 tenant-aware application-context provider: request-scoped
/// memoization, tenant isolation, and the fail-closed 401/503 mapping with no ownership disclosure. The
/// production scoped <c>CachedApplicationContextProvider</c> stays wired; only the CMS-facing
/// <c>IConfigurationServiceApplicationProvider</c> underneath it is replaced.
/// </summary>
public sealed class Given_Postgresql_ApplicationContextIntegration : PostgresqlApiIntegrationTestBase
{
    private const string CallCountTenant = "app-context-call-count-tenant";
    private const string FirstIsolationTenant = "app-context-tenant-a";
    private const string SecondIsolationTenant = "app-context-tenant-b";
    private const string NotFoundTenant = "app-context-not-found-tenant";
    private const string UnavailableTenant = "app-context-unavailable-tenant";

    private readonly RecordingConfigurationServiceApplicationProvider _applicationContextProvider = new(
        Resolve
    );

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override bool MultiTenancy => true;

    protected override IConfigurationServiceApplicationProvider? ApplicationContextConfigurationProviderOverride =>
        _applicationContextProvider;

    [Test]
    public Task It_resolves_application_context_at_most_once_per_request() =>
        ApplicationContextIntegrationScenario.It_resolves_application_context_at_most_once_per_request(
            Harness,
            _applicationContextProvider,
            CallCountTenant
        );

    [Test]
    public Task It_resolves_independent_contexts_per_tenant() =>
        ApplicationContextIntegrationScenario.It_resolves_independent_contexts_per_tenant(
            Harness,
            _applicationContextProvider,
            FirstIsolationTenant,
            SecondIsolationTenant
        );

    [Test]
    public Task It_maps_not_found_to_401_without_disclosure() =>
        ApplicationContextIntegrationScenario.It_maps_not_found_to_401_without_disclosure(
            Harness,
            NotFoundTenant
        );

    [Test]
    public Task It_maps_unavailable_to_503_without_disclosure() =>
        ApplicationContextIntegrationScenario.It_maps_unavailable_to_503_without_disclosure(
            Harness,
            UnavailableTenant
        );

    private static ApplicationContextResult Resolve(string clientId, string? tenant) =>
        tenant switch
        {
            NotFoundTenant => new ApplicationContextResult.NotFound(),
            UnavailableTenant => new ApplicationContextResult.Unavailable(),
            FirstIsolationTenant => Success(applicationId: 201),
            SecondIsolationTenant => Success(applicationId: 202),
            _ => Success(applicationId: 200),
        };

    private static ApplicationContextResult Success(long applicationId) =>
        new ApplicationContextResult.Success(
            new ApplicationContext(
                Id: applicationId,
                ApplicationId: applicationId,
                ClientId: ExternalDoublesConstants.SmokeClientId,
                ClientUuid: ExternalDoublesConstants.StableClientUuid,
                DataStoreIds: [ExternalDoublesConstants.StableDataStoreId],
                CreatorOwnershipTokenId: null,
                OwnershipTokenIds: []
            )
        );
}
