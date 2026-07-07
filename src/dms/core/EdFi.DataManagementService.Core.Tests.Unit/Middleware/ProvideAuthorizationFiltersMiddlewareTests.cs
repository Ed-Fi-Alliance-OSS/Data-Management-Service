// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Tests.Unit.Handler;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProvideAuthorizationFiltersMiddlewareTests
{
    private static ClientAuthorizations CreateClientAuthorizations() =>
        new(
            TokenId: "token123",
            ClientId: "client123",
            ClaimSetName: "TestClaimSet",
            EducationOrganizationIds: [],
            NamespacePrefixes: [],
            DataStoreIds: []
        );

    private static PathComponents CreatePathComponents() =>
        new(
            ProjectEndpointName: new ProjectEndpointName("ed-fi"),
            EndpointName: new EndpointName("students"),
            DocumentUuid: No.DocumentUuid
        );

    private static RequestInfo CreateAuthorizedRequestInfo()
    {
        RequestInfo requestInfo = No.RequestInfo("traceId");
        requestInfo.ClientAuthorizations = CreateClientAuthorizations();
        requestInfo.PathComponents = CreatePathComponents();
        requestInfo.ResourceActionAuthStrategies =
        [
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            AuthorizationStrategyNameConstants.NamespaceBased,
            AuthorizationStrategyNameConstants.OwnershipBased,
            "StudentWithSectionEnrollments",
        ];
        return requestInfo;
    }

    private static async Task Execute(RequestInfo requestInfo, Action onNext)
    {
        var middleware = new ProvideAuthorizationFiltersMiddleware(NullLogger.Instance);

        await middleware.Execute(
            requestInfo,
            () =>
            {
                onNext();
                return Task.CompletedTask;
            }
        );
    }

    private static void AssertRawStrategyEvaluators(RequestInfo requestInfo)
    {
        requestInfo
            .AuthorizationStrategyEvaluators.Select(static evaluator => evaluator.AuthorizationStrategyName)
            .Should()
            .Equal(requestInfo.ResourceActionAuthStrategies);

        requestInfo
            .AuthorizationStrategyEvaluators.Select(static evaluator => evaluator.Filters)
            .Should()
            .OnlyContain(static filters => filters.Length == 0);

        requestInfo
            .AuthorizationStrategyEvaluators.Select(static evaluator => evaluator.Operator)
            .Should()
            .OnlyContain(static filterOperator => filterOperator == FilterOperator.Or);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_Without_A_Mapping_Set : ProvideAuthorizationFiltersMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = CreateAuthorizedRequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            await Execute(_requestInfo, () => _nextCalled = true);
        }

        [Test]
        public void It_calls_the_next_middleware_step()
        {
            _nextCalled.Should().BeTrue();
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_preserves_raw_strategy_names_with_empty_filters()
        {
            _requestInfo.MappingSet.Should().BeNull();
            AssertRawStrategyEvaluators(_requestInfo);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_A_Relational_Mapping_Set : ProvideAuthorizationFiltersMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = CreateAuthorizedRequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Execute(_requestInfo, () => _nextCalled = true);
        }

        [Test]
        public void It_calls_the_next_middleware_step()
        {
            _nextCalled.Should().BeTrue();
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_preserves_raw_strategy_names_with_empty_filters()
        {
            AssertRawStrategyEvaluators(_requestInfo);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Client_Authorizations : ProvideAuthorizationFiltersMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo("traceId");
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ResourceActionAuthStrategies =
            [
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
            ];

            await Execute(_requestInfo, () => _nextCalled = true);
        }

        [Test]
        public void It_does_not_call_the_next_middleware_step()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_unauthorized()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(401);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            // Pin the full authentication-failure body contract on the identical shared shape
            // the sibling ResourceActionAuthorizationMiddleware path asserts.
            TestHelper.AssertUnauthorizedProblemDetails(
                _requestInfo.FrontendResponse,
                "No authorization information found. Ensure valid JWT token is provided."
            );
        }

        [Test]
        public void It_includes_www_authenticate_header()
        {
            _requestInfo.FrontendResponse.Headers.Should().ContainKey("WWW-Authenticate");
            _requestInfo
                .FrontendResponse.Headers["WWW-Authenticate"]
                .Should()
                .Be("Bearer error=\"invalid_token\"");
        }
    }
}
