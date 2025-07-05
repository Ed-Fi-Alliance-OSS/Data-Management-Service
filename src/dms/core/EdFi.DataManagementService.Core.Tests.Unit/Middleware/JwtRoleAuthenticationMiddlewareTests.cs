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
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class JwtRoleAuthenticationMiddlewareTests
{
    internal static (
        JwtRoleAuthenticationMiddleware middleware,
        IJwtValidationService jwtValidationService,
        IOptions<JwtAuthenticationOptions> options
    ) CreateMiddleware(string? clientRole = "service")
    {
        var jwtValidationService = A.Fake<IJwtValidationService>();
        var logger = A.Fake<ILogger<JwtRoleAuthenticationMiddleware>>();
        var authOptions = new JwtAuthenticationOptions { ClientRole = clientRole ?? string.Empty };
        var options = A.Fake<IOptions<JwtAuthenticationOptions>>();
        A.CallTo(() => options.Value).Returns(authOptions);

        var middleware = new JwtRoleAuthenticationMiddleware(jwtValidationService, logger, options);
        return (middleware, jwtValidationService, options);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_Without_Authorization_Header : JwtRoleAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/test",
                Body: "{}",
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                QueryParameters: [],
                TraceId: new TraceId("trace123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var (middleware, _, _) = CreateMiddleware();

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
            _requestInfo.FrontendResponse.Should().NotBeNull();
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(401);
        }

        [Test]
        public void It_includes_www_authenticate_header()
        {
            _requestInfo.FrontendResponse!.Headers.Should().ContainKey("WWW-Authenticate");
            _requestInfo
                .FrontendResponse.Headers["WWW-Authenticate"]
                .Should()
                .Be("Bearer error=\"invalid_token\"");
        }

        [Test]
        public void It_returns_problem_json_content_type()
        {
            _requestInfo.FrontendResponse!.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_includes_error_detail_in_response_body()
        {
            _requestInfo.FrontendResponse!.Body?.ToString().Should().Contain("Bearer token required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Invalid_Authorization_Format : JwtRoleAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/test",
                Body: "{}",
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Invalid token",
                },
                QueryParameters: [],
                TraceId: new TraceId("trace123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var (middleware, _, _) = CreateMiddleware();

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
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(401);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Invalid_Bearer_Token : JwtRoleAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/test",
                Body: "{}",
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer invalid-token",
                },
                QueryParameters: [],
                TraceId: new TraceId("trace123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var (middleware, jwtValidationService, _) = CreateMiddleware();

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "invalid-token",
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns((null, null));

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
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(401);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_Without_Required_Role : JwtRoleAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/test",
                Body: "{}",
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer valid-token",
                },
                QueryParameters: [],
                TraceId: new TraceId("trace123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, "wrong-role"),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            var clientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClaimSetName: "test",
                EducationOrganizationIds: [],
                NamespacePrefixes: []
            );

            var (middleware, jwtValidationService, _) = CreateMiddleware();

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "valid-token",
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns((principal, clientAuthorizations));

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
        public void It_returns_403_forbidden()
        {
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_problem_json_content_type()
        {
            _requestInfo.FrontendResponse!.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_includes_error_detail_in_response_body()
        {
            _requestInfo.FrontendResponse!.Body?.ToString().Should().Contain("Insufficient permissions");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_With_Required_Role : JwtRoleAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/test",
                Body: "{}",
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer valid-token",
                },
                QueryParameters: [],
                TraceId: new TraceId("trace123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, "service"),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            var clientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClaimSetName: "test",
                EducationOrganizationIds: [],
                NamespacePrefixes: []
            );

            var (middleware, jwtValidationService, _) = CreateMiddleware();

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "valid-token",
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns((principal, clientAuthorizations));

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
        public void It_does_not_set_a_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_When_No_Role_Is_Required : JwtRoleAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/test",
                Body: "{}",
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer valid-token",
                },
                QueryParameters: [],
                TraceId: new TraceId("trace123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var claims = new List<Claim> { new Claim(ClaimTypes.Name, "test-user") };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            var clientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClaimSetName: "test",
                EducationOrganizationIds: [],
                NamespacePrefixes: []
            );

            var (middleware, jwtValidationService, _) = CreateMiddleware(clientRole: string.Empty);

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "valid-token",
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns((principal, clientAuthorizations));

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
        public void It_does_not_set_a_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Case_Insensitive_Authorization_Header
        : JwtRoleAuthenticationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/test",
                Body: "{}",
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["authorization"] = "Bearer valid-token", // lowercase
                },
                QueryParameters: [],
                TraceId: new TraceId("trace123")
            );
            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, "service"),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            var clientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClaimSetName: "test",
                EducationOrganizationIds: [],
                NamespacePrefixes: []
            );

            var (middleware, jwtValidationService, _) = CreateMiddleware();

            A.CallTo(() =>
                    jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        "valid-token",
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns((principal, clientAuthorizations));

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
        public void It_does_not_set_a_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
