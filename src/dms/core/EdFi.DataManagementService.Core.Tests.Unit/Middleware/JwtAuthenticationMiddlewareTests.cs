// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class JwtAuthenticationMiddlewareTests
{
    internal static (
        JwtAuthenticationMiddleware middleware,
        IJwtValidationService jwtValidationService
    ) CreateMiddleware()
    {
        var jwtValidationService = A.Fake<IJwtValidationService>();
        var logger = A.Fake<ILogger<JwtAuthenticationMiddleware>>();
        var middleware = new JwtAuthenticationMiddleware(jwtValidationService, logger);
        return (middleware, jwtValidationService);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Valid_Bearer_Token : JwtAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;
        private ClientAuthorizations _expectedAuthorizations = null!;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer valid-token",
                },
                QueryParameters: [],
                TraceId: new TraceId("123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var expectedPrincipal = new ClaimsPrincipal();
            _expectedAuthorizations = new ClientAuthorizations(
                ClientId: "",
                TokenId: "token-123",
                ClaimSetName: "edfi-admin",
                EducationOrganizationIds: new List<EducationOrganizationId>(),
                NamespacePrefixes: new List<NamespacePrefix>()
            );

            var (middleware, jwtValidationService) = CreateMiddleware();

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "valid-token",
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                        (expectedPrincipal, _expectedAuthorizations)
                    )
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
        public void It_calls_the_next_middleware()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_client_authorizations_in_request_info()
        {
            _requestInfo.ClientAuthorizations.Should().Be(_expectedAuthorizations);
        }

        [Test]
        public void It_does_not_set_a_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Invalid_Bearer_Token : JwtAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer invalid-token",
                },
                QueryParameters: [],
                TraceId: new TraceId("123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var (middleware, jwtValidationService) = CreateMiddleware();

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "invalid-token",
                        A<CancellationToken>._
                    )
                )
                .Returns(Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>((null, null)));

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_401_unauthorized()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(401);
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

        [Test]
        public void It_returns_problem_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_includes_error_detail_in_response_body()
        {
            _requestInfo.FrontendResponse.Body?.ToString().Should().Contain("Invalid token");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_Without_Authorization_Header : JwtAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                QueryParameters: [],
                TraceId: new TraceId("123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var (middleware, _) = CreateMiddleware();

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_401_unauthorized()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(401);
        }

        [Test]
        public void It_includes_error_detail_in_response_body()
        {
            _requestInfo.FrontendResponse.Body?.ToString().Should().Contain("Bearer token required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Non_Bearer_Authorization : JwtAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Basic dXNlcjpwYXNz",
                },
                QueryParameters: [],
                TraceId: new TraceId("123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var (middleware, _) = CreateMiddleware();

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_401_unauthorized()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(401);
        }

        [Test]
        public void It_includes_error_detail_in_response_body()
        {
            _requestInfo.FrontendResponse.Body?.ToString().Should().Contain("Bearer token required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Case_Insensitive_Authorization_Header : JwtAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;
        private ClientAuthorizations _expectedAuthorizations = null!;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["authorization"] = "Bearer valid-token",
                }, // lowercase
                QueryParameters: [],
                TraceId: new TraceId("123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var expectedPrincipal = new ClaimsPrincipal();
            _expectedAuthorizations = new ClientAuthorizations(
                ClientId: "",
                TokenId: "token-123",
                ClaimSetName: "edfi-admin",
                EducationOrganizationIds: new List<EducationOrganizationId>(),
                NamespacePrefixes: new List<NamespacePrefix>()
            );

            var (middleware, jwtValidationService) = CreateMiddleware();

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "valid-token",
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                        (expectedPrincipal, _expectedAuthorizations)
                    )
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
        public void It_calls_the_next_middleware()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_client_authorizations_in_request_info()
        {
            _requestInfo.ClientAuthorizations.Should().Be(_expectedAuthorizations);
        }
    }
}
