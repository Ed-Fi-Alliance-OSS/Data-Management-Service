// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProvideAuthorizationFiltersMiddlewareTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Authorization_Filter_Provider_Throws : ProvideAuthorizationFiltersMiddlewareTests
    {
        private FrontendResponse _response = null!;

        private static void AssertLegacyServerErrorResponse(
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

            requestInfo.ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClientId: "client123",
                ClaimSetName: "TestClaimSet",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DmsInstanceIds: []
            );
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
        public void It_returns_legacy_500_body()
        {
            AssertLegacyServerErrorResponse(
                _response,
                "Error while authorizing the request.simulated failure",
                "traceId"
            );
        }
    }
}
