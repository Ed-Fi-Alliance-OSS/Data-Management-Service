// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// D6/A11: ValidateClientTenantBindingMiddleware resolves IApplicationContextProvider from the
/// request scope, confirms the authenticated client's application is bound to the URL tenant, stores
/// nothing from the resolved context (no RequestInfo.ApplicationContext assignment, DataStoreIds never
/// inspected), and reuses ApplicationContextFailureResponseFactory for NotFound (401) and Unavailable
/// (503). Two tenants both carry an identically named claim set granting the identity claim, so a
/// token bound to Tenant A on Tenant B's route fails at binding before ServiceClaimAuthorizationMiddleware
/// would ever consult IClaimSetProvider.
/// </summary>
public class ValidateClientTenantBindingMiddlewareTests
{
    private const string ClientId = "identity-client-1";
    private const string TenantA = "TenantA";
    private const string TenantB = "TenantB";
    private const string ClaimSetName = "IdentityClaims";
    private static readonly string _identityClaimUri = $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity";

    private static readonly ApplicationContext _applicationContextWithEmptyDataStoreIds = new(
        Id: 1,
        ApplicationId: 2,
        ClientId,
        ClientUuid: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        DataStoreIds: [],
        CreatorOwnershipTokenId: null,
        OwnershipTokenIds: []
    );

    private static ValidateClientTenantBindingMiddleware CreateMiddleware() =>
        new(NullLogger<ValidateClientTenantBindingMiddleware>.Instance);

    private static RequestInfo CreateRequestInfo(
        string tenant,
        IApplicationContextProvider applicationContextProvider,
        IClaimSetProvider claimSetProvider
    )
    {
        IServiceProvider scopedServiceProvider = new ServiceCollection()
            .AddSingleton(applicationContextProvider)
            .AddSingleton(claimSetProvider)
            .BuildServiceProvider();

        var frontendRequest = new FrontendRequest(
            Path: "/identity/v2/identities",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("client-tenant-binding"),
            RouteQualifiers: [],
            Tenant: tenant
        );

        return new RequestInfo(frontendRequest, RequestMethod.POST, scopedServiceProvider)
        {
            ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token-id",
                ClientId,
                ClaimSetName: ClaimSetName,
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DataStoreIds: []
            ),
        };
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Token_Bound_To_A_Different_Tenant
    {
        private RequestInfo _requestInfo = null!;
        private IApplicationContextProvider _applicationContextProvider = null!;
        private IClaimSetProvider _claimSetProvider = null!;
        private bool _serviceClaimAuthorizationReached;

        private static ClaimSet IdentityGrantingClaimSet() =>
            new(
                ClaimSetName,
                [
                    new ResourceClaim(
                        _identityClaimUri,
                        "Create",
                        [
                            new AuthorizationStrategy(
                                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                            ),
                        ]
                    ),
                    new ResourceClaim(
                        _identityClaimUri,
                        "Read",
                        [
                            new AuthorizationStrategy(
                                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                            ),
                        ]
                    ),
                ]
            );

        [SetUp]
        public async Task Setup()
        {
            _applicationContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() =>
                    _applicationContextProvider.GetApplicationByClientIdAsync(
                        ClientId,
                        TenantB,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ApplicationContextResult.NotFound());

            // Tenant A and Tenant B both carry an identically named claim set granting identity, so
            // if the binding step let the request through, authorization would have succeeded.
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(TenantA, A<CancellationToken>._))
                .Returns((IList<ClaimSet>)[IdentityGrantingClaimSet()]);
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(TenantB, A<CancellationToken>._))
                .Returns((IList<ClaimSet>)[IdentityGrantingClaimSet()]);

            // The token was minted for Tenant A but the request was routed to Tenant B.
            _requestInfo = CreateRequestInfo(TenantB, _applicationContextProvider, _claimSetProvider);

            var serviceClaimAuthorizationMiddleware = new ServiceClaimAuthorizationMiddleware(
                _claimSetProvider,
                NullLogger<ServiceClaimAuthorizationMiddleware>.Instance
            );

            await CreateMiddleware()
                .Execute(
                    _requestInfo,
                    async () =>
                    {
                        await serviceClaimAuthorizationMiddleware.Execute(
                            _requestInfo,
                            () =>
                            {
                                _serviceClaimAuthorizationReached = true;
                                return Task.CompletedTask;
                            }
                        );
                    }
                );
        }

        [Test]
        public void It_returns_401()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(401);
        }

        [Test]
        public void It_returns_the_authentication_failure_problem_details()
        {
            TestHelper.AssertUnauthorizedProblemDetails(
                _requestInfo.FrontendResponse,
                "Unable to resolve application context for the authenticated client."
            );
        }

        [Test]
        public void It_carries_the_www_authenticate_header()
        {
            _requestInfo
                .FrontendResponse.Headers.Should()
                .ContainKey("WWW-Authenticate")
                .WhoseValue.Should()
                .Be("Bearer error=\"invalid_token\"");
        }

        [Test]
        public void It_never_reaches_service_claim_authorization()
        {
            _serviceClaimAuthorizationReached.Should().BeFalse();
        }

        [Test]
        public void It_never_calls_IClaimSetProvider()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void It_stores_no_application_context()
        {
            _requestInfo.ApplicationContext.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Binding_Lookup_Is_Unavailable
    {
        private RequestInfo _requestInfo = null!;
        private IApplicationContextProvider _applicationContextProvider = null!;
        private IClaimSetProvider _claimSetProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            _applicationContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() =>
                    _applicationContextProvider.GetApplicationByClientIdAsync(
                        ClientId,
                        TenantA,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ApplicationContextResult.Unavailable());
            _claimSetProvider = A.Fake<IClaimSetProvider>();

            _requestInfo = CreateRequestInfo(TenantA, _applicationContextProvider, _claimSetProvider);

            await CreateMiddleware().Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_returns_503()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_the_service_unavailable_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:service-unavailable");
        }

        [Test]
        public void It_never_calls_IClaimSetProvider()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Client_Is_Bound_To_The_Requested_Tenant_With_Empty_DataStoreIds
    {
        private RequestInfo _requestInfo = null!;
        private IApplicationContextProvider _applicationContextProvider = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _applicationContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() =>
                    _applicationContextProvider.GetApplicationByClientIdAsync(
                        ClientId,
                        TenantA,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ApplicationContextResult.Success(_applicationContextWithEmptyDataStoreIds));

            _requestInfo = CreateRequestInfo(
                TenantA,
                _applicationContextProvider,
                A.Fake<IClaimSetProvider>()
            );

            await CreateMiddleware()
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
        public void It_produces_no_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_stores_nothing_from_the_resolved_context()
        {
            _requestInfo.ApplicationContext.Should().BeNull();
        }

        [Test]
        public void It_called_the_provider_with_the_request_tenant_and_client_id()
        {
            A.CallTo(() =>
                    _applicationContextProvider.GetApplicationByClientIdAsync(
                        ClientId,
                        TenantA,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }
}
