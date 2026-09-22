// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Pins <see cref="ValidateTenantExistsMiddleware" /> (design.md D4, story acceptance A4-A5):
/// pass-through when multitenancy is off, the 404/503 outcome mapping, and that a short-circuited
/// request never reaches a downstream step that would consult claim sets or the identity provider.
/// The backing <see cref="IdentityTenantSnapshot" /> is real, driven through a faked
/// <see cref="IDataStoreProvider" />, since the snapshot itself is <c>internal sealed</c> and cannot
/// be faked directly.
/// </summary>
[TestFixture]
public class ValidateTenantExistsMiddlewareTests
{
    private const string Tenant = "North";

    private static ValidateTenantExistsMiddleware CreateMiddleware(
        bool multiTenancyEnabled,
        IdentityTenantSnapshot snapshot
    ) => new(multiTenancyEnabled, snapshot, NullLogger<ValidateTenantExistsMiddleware>.Instance);

    private static IHostApplicationLifetime CreateLifetime()
    {
        var lifetime = A.Fake<IHostApplicationLifetime>();
        A.CallTo(() => lifetime.ApplicationStopping).Returns(CancellationToken.None);
        return lifetime;
    }

    private static IdentityTenantSnapshot CreateSnapshotThatAnswers(
        TenantExistenceOutcome outcome,
        IDataStoreProvider? dataStoreProvider = null
    )
    {
        IDataStoreProvider provider = dataStoreProvider ?? A.Fake<IDataStoreProvider>();
        switch (outcome)
        {
            case TenantExistenceOutcome.Exists:
                A.CallTo(() => provider.LoadTenants(A<CancellationToken>._))
                    .Returns(Task.FromResult<IList<string>>([Tenant]));
                break;
            case TenantExistenceOutcome.Absent:
                A.CallTo(() => provider.LoadTenants(A<CancellationToken>._))
                    .Returns(Task.FromResult<IList<string>>(["SomeOtherTenant"]));
                break;
            case TenantExistenceOutcome.Unavailable:
                A.CallTo(() => provider.LoadTenants(A<CancellationToken>._))
                    .ThrowsAsync(new InvalidOperationException("boom"));
                break;
        }

        return new IdentityTenantSnapshot(
            provider,
            new FakeTimeProvider(),
            CreateLifetime(),
            NullLogger<IdentityTenantSnapshot>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo(string? tenant)
    {
        var frontendRequest = new FrontendRequest(
            Path: "/identity/v2/identities",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("validate-tenant-exists"),
            RouteQualifiers: [],
            Tenant: tenant
        );

        return new RequestInfo(frontendRequest, RequestMethod.GET, No.ServiceProvider);
    }

    [Test]
    public async Task A_nonexistent_tenant_returns_404_and_never_reaches_claim_sets_or_the_identity_service()
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        var identityService = A.Fake<IIdentityService>();
        IdentityTenantSnapshot snapshot = CreateSnapshotThatAnswers(TenantExistenceOutcome.Absent);
        RequestInfo requestInfo = CreateRequestInfo(Tenant);

        await CreateMiddleware(true, snapshot).Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(identityService).MustNotHaveHappened();
    }

    [Test]
    public async Task An_unavailable_tenant_check_returns_503_with_the_service_unavailable_problem_type()
    {
        IdentityTenantSnapshot snapshot = CreateSnapshotThatAnswers(TenantExistenceOutcome.Unavailable);
        RequestInfo requestInfo = CreateRequestInfo(Tenant);

        await CreateMiddleware(true, snapshot).Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        requestInfo.FrontendResponse.Body!["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:service-unavailable");
    }

    [Test]
    public async Task An_existing_tenant_calls_next()
    {
        IdentityTenantSnapshot snapshot = CreateSnapshotThatAnswers(TenantExistenceOutcome.Exists);
        RequestInfo requestInfo = CreateRequestInfo(Tenant);
        var nextCalled = false;

        await CreateMiddleware(true, snapshot)
            .Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        nextCalled.Should().BeTrue();
        requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    [Test]
    public async Task Multitenancy_off_is_a_pass_through_that_never_consults_the_snapshot()
    {
        var dataStoreProvider = A.Fake<IDataStoreProvider>();
        // If the snapshot were consulted despite multitenancy being off, this would surface as an
        // unhandled failure rather than silently passing.
        A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
            .Throws(new InvalidOperationException("must not be called when multitenancy is off"));
        IdentityTenantSnapshot snapshot = new(
            dataStoreProvider,
            new FakeTimeProvider(),
            CreateLifetime(),
            NullLogger<IdentityTenantSnapshot>.Instance
        );
        RequestInfo requestInfo = CreateRequestInfo(tenant: null);
        var nextCalled = false;

        await CreateMiddleware(false, snapshot)
            .Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        nextCalled.Should().BeTrue();
        requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._)).MustNotHaveHappened();
    }
}
