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

    [TestFixture]
    [Parallelizable]
    public class Given_A_Nonexistent_Tenant
    {
        private RequestInfo _requestInfo = null!;
        private IClaimSetProvider _claimSetProvider = null!;
        private IIdentityService _identityService = null!;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            _identityService = A.Fake<IIdentityService>();
            IdentityTenantSnapshot snapshot = CreateSnapshotThatAnswers(TenantExistenceOutcome.Absent);
            _requestInfo = CreateRequestInfo(Tenant);

            await CreateMiddleware(true, snapshot).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_returns_404()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_returns_the_problem_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_never_reaches_claim_sets()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void It_never_reaches_the_identity_service()
        {
            A.CallTo(_identityService).MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unavailable_Tenant_Check
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            IdentityTenantSnapshot snapshot = CreateSnapshotThatAnswers(TenantExistenceOutcome.Unavailable);
            _requestInfo = CreateRequestInfo(Tenant);

            await CreateMiddleware(true, snapshot).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_returns_503()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_the_service_unavailable_problem_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:service-unavailable");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Existing_Tenant
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            IdentityTenantSnapshot snapshot = CreateSnapshotThatAnswers(TenantExistenceOutcome.Exists);
            _requestInfo = CreateRequestInfo(Tenant);

            await CreateMiddleware(true, snapshot)
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_leaves_the_response_untouched()
        {
            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multitenancy_Off
    {
        private RequestInfo _requestInfo = null!;
        private IDataStoreProvider _dataStoreProvider = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            // If the snapshot were consulted despite multitenancy being off, this would surface as an
            // unhandled failure rather than silently passing.
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Throws(new InvalidOperationException("must not be called when multitenancy is off"));
            IdentityTenantSnapshot snapshot = new(
                _dataStoreProvider,
                new FakeTimeProvider(),
                CreateLifetime(),
                NullLogger<IdentityTenantSnapshot>.Instance
            );
            _requestInfo = CreateRequestInfo(tenant: null);

            await CreateMiddleware(false, snapshot)
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_leaves_the_response_untouched()
        {
            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }

        [Test]
        public void It_never_consults_the_snapshot()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._)).MustNotHaveHappened();
        }
    }
}
