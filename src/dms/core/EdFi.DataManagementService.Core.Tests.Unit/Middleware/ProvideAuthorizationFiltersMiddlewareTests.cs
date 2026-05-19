// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using EdFi.DataManagementService.Core.Tests.Unit.Handler;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProvideAuthorizationFiltersMiddlewareTests
{
    private static ClientAuthorizations CreateClientAuthorizations(
        List<EducationOrganizationId>? educationOrganizationIds = null,
        List<NamespacePrefix>? namespacePrefixes = null
    ) =>
        new(
            TokenId: "token123",
            ClientId: "client123",
            ClaimSetName: "TestClaimSet",
            EducationOrganizationIds: educationOrganizationIds ?? [],
            NamespacePrefixes: namespacePrefixes ?? [],
            DmsInstanceIds: []
        );

    private static PathComponents CreateGetManyPathComponents() =>
        new(
            ProjectEndpointName: new ProjectEndpointName("ed-fi"),
            EndpointName: new EndpointName("students"),
            DocumentUuid: No.DocumentUuid
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_Relational_Get_Many_Request : ProvideAuthorizationFiltersMiddlewareTests
    {
        private readonly IAuthorizationServiceFactory _authorizationServiceFactory =
            A.Fake<IAuthorizationServiceFactory>();

        private readonly RequestInfo _requestInfo = No.RequestInfo("traceId");
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);
            _requestInfo.ClientAuthorizations = CreateClientAuthorizations();
            _requestInfo.PathComponents = CreateGetManyPathComponents();
            _requestInfo.ResourceActionAuthStrategies =
            [
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                AuthorizationStrategyNameConstants.NamespaceBased,
                AuthorizationStrategyNameConstants.OwnershipBased,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                "StudentWithSectionEnrollments",
            ];

            var middleware = new ProvideAuthorizationFiltersMiddleware(
                _authorizationServiceFactory,
                NullLogger.Instance
            );

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_the_next_middleware_step()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_preserves_raw_strategy_names_with_empty_filters()
        {
            _requestInfo
                .AuthorizationStrategyEvaluators.Select(static evaluator =>
                    evaluator.AuthorizationStrategyName
                )
                .Should()
                .Equal(_requestInfo.ResourceActionAuthStrategies);

            _requestInfo
                .AuthorizationStrategyEvaluators.Select(static evaluator => evaluator.Filters)
                .Should()
                .OnlyContain(static filters => filters.Length == 0);

            _requestInfo
                .AuthorizationStrategyEvaluators.Select(static evaluator => evaluator.Operator)
                .Should()
                .OnlyContain(static filterOperator => filterOperator == FilterOperator.Or);
        }

        [Test]
        public void It_does_not_resolve_filter_providers()
        {
            A.CallTo(() =>
                    _authorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
                        A<string>._,
                        A<IServiceProvider>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Legacy_Get_Many_Request_With_A_Missing_Filter_Provider
        : ProvideAuthorizationFiltersMiddlewareTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var authorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
            var requestInfo = No.RequestInfo("traceId");

            requestInfo.ClientAuthorizations = CreateClientAuthorizations();
            requestInfo.PathComponents = CreateGetManyPathComponents();
            requestInfo.ResourceActionAuthStrategies = ["StudentWithSectionEnrollments"];

            A.CallTo(() =>
                    authorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
                        "StudentWithSectionEnrollments",
                        requestInfo.ScopedServiceProvider
                    )
                )
                .Returns(null);

            var middleware = new ProvideAuthorizationFiltersMiddleware(
                authorizationServiceFactory,
                NullLogger.Instance
            );

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            _response = (FrontendResponse)requestInfo.FrontendResponse;
        }

        [Test]
        public void It_returns_forbidden()
        {
            _response.StatusCode.Should().Be(403);
            _response.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_reports_the_missing_provider()
        {
            JsonObject body = _response.Body!.AsObject();
            JsonArray errors = body["errors"]!.AsArray();

            errors.Should().ContainSingle();
            errors[0]
                ?.GetValue<string>()
                .Should()
                .Be(
                    "Could not find authorization filters implementation for the following strategy: 'StudentWithSectionEnrollments'."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Relational_Get_By_Id_Request_With_Empty_EdOrg_Claims
        : ProvideAuthorizationFiltersMiddlewareTests
    {
        private FrontendResponse _response = null!;

        private static PathComponents CreateGetByIdPathComponents() =>
            new(
                ProjectEndpointName: new ProjectEndpointName("ed-fi"),
                EndpointName: new EndpointName("students"),
                DocumentUuid: new DocumentUuid(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
                HasDocumentUuidSegment: true
            );

        [SetUp]
        public async Task Setup()
        {
            var authorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
            var requestInfo = No.RequestInfo("traceId");

            requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);
            requestInfo.ClientAuthorizations = CreateClientAuthorizations();
            requestInfo.PathComponents = CreateGetByIdPathComponents();
            requestInfo.ResourceActionAuthStrategies =
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            ];

            A.CallTo(() =>
                    authorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        requestInfo.ScopedServiceProvider
                    )
                )
                .Returns(new RelationshipsWithEdOrgsOnlyFiltersProvider());

            var middleware = new ProvideAuthorizationFiltersMiddleware(
                authorizationServiceFactory,
                NullLogger.Instance
            );

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            _response = (FrontendResponse)requestInfo.FrontendResponse;
        }

        [Test]
        public void It_still_uses_provider_validation()
        {
            _response.StatusCode.Should().Be(403);
            _response.ContentType.Should().Be("application/problem+json");

            JsonObject body = _response.Body!.AsObject();
            JsonArray errors = body["errors"]!.AsArray();

            errors.Should().ContainSingle();
            errors[0]
                ?.GetValue<string>()
                .Should()
                .Be(
                    "The API client has been given permissions on a resource that uses the 'RelationshipsWithEdOrgsOnly' authorization strategy but the client doesn't have any education organizations assigned."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Authorization_Filter_Provider_Throws : ProvideAuthorizationFiltersMiddlewareTests
    {
        private FrontendResponse _response = null!;

        private static void AssertExpectedServerErrorResponse(
            IFrontendResponse response,
            string expectedMessage,
            string expectedTraceId
        )
        {
            response.StatusCode.Should().Be(500);
            response.ContentType.Should().Be("application/json");

            JsonObject body = response.Body!.AsObject();

            body.Select(property => property.Key).Should().BeEquivalentTo("message", "traceId");
            body["message"]?.GetValue<string>().Should().Be(expectedMessage);
            body["traceId"]?.GetValue<string>().Should().Be(expectedTraceId);
            body["detail"].Should().BeNull();
            body["type"].Should().BeNull();
            body["title"].Should().BeNull();
            body["status"].Should().BeNull();
            body["correlationId"].Should().BeNull();
        }

        [SetUp]
        public async Task Setup()
        {
            var authorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
            var authorizationFiltersProvider = A.Fake<IAuthorizationFiltersProvider>();
            var requestInfo = No.RequestInfo("traceId");

            requestInfo.ClientAuthorizations = CreateClientAuthorizations();
            requestInfo.ResourceActionAuthStrategies = ["TestStrategy"];

            A.CallTo(() =>
                    authorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
                        "TestStrategy",
                        requestInfo.ScopedServiceProvider
                    )
                )
                .Returns(authorizationFiltersProvider);
            A.CallTo(() => authorizationFiltersProvider.GetFilters(requestInfo.ClientAuthorizations))
                .Throws(new InvalidOperationException("simulated failure"));

            var middleware = new ProvideAuthorizationFiltersMiddleware(
                authorizationServiceFactory,
                NullLogger.Instance
            );

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            _response = (FrontendResponse)requestInfo.FrontendResponse;
        }

        [Test]
        public void It_returns_the_expected_500_body()
        {
            AssertExpectedServerErrorResponse(
                _response,
                "Error while authorizing the request.simulated failure",
                "traceId"
            );
        }
    }
}
