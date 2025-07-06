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
public class JwtRoleAuthenticationMiddlewareTests
{
    private IJwtValidationService _jwtValidationService = null!;
    private ILogger<JwtRoleAuthenticationMiddleware> _logger = null!;
    private JwtAuthenticationOptions _options = null!;
    private JwtRoleAuthenticationMiddleware _middleware = null!;
    private FrontendRequest _frontendRequest = null!;
    private RequestData _requestData = null!;

    [SetUp]
    public void Setup()
    {
        _jwtValidationService = A.Fake<IJwtValidationService>();
        _logger = A.Fake<ILogger<JwtRoleAuthenticationMiddleware>>();
        _options = new JwtAuthenticationOptions { Enabled = true, ClientRole = "service" };
        var optionsWrapper = A.Fake<IOptions<JwtAuthenticationOptions>>();
        A.CallTo(() => optionsWrapper.Value).Returns(_options);

        _middleware = new JwtRoleAuthenticationMiddleware(_jwtValidationService, _logger, optionsWrapper);

        _frontendRequest = new FrontendRequest(
            Body: "{}",
            Headers: new Dictionary<string, string>(),
            Path: "/test",
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("trace123")
        );
        _requestData = new RequestData(_frontendRequest, RequestMethod.GET);
    }

    [Test]
    public async Task When_authentication_is_disabled_should_pass_through()
    {
        // Arrange
        _options.Enabled = false;
        // Recreate middleware with updated options
        var optionsWrapper = A.Fake<IOptions<JwtAuthenticationOptions>>();
        A.CallTo(() => optionsWrapper.Value).Returns(_options);
        _middleware = new JwtRoleAuthenticationMiddleware(_jwtValidationService, _logger, optionsWrapper);

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(_requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        _requestData.FrontendResponse.Should().Be(No.FrontendResponse);
    }

    [Test]
    public async Task When_authorization_header_is_missing_should_return_401()
    {
        // Arrange
        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(_requestData, next);

        // Assert
        nextCalled.Should().BeFalse();
        _requestData.FrontendResponse.Should().NotBeNull();
        _requestData.FrontendResponse!.StatusCode.Should().Be(401);
        _requestData.FrontendResponse!.Headers.Should().ContainKey("WWW-Authenticate");
        _requestData.FrontendResponse!.ContentType.Should().Be("application/problem+json");
    }

    [Test]
    public async Task When_authorization_header_is_invalid_format_should_return_401()
    {
        // Arrange
        _frontendRequest = _frontendRequest with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Invalid token" },
        };
        _requestData = new RequestData(_frontendRequest, RequestMethod.GET);

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(_requestData, next);

        // Assert
        nextCalled.Should().BeFalse();
        _requestData.FrontendResponse!.StatusCode.Should().Be(401);
    }

    [Test]
    public async Task When_token_validation_fails_should_return_401()
    {
        // Arrange
        _frontendRequest = _frontendRequest with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer invalid-token" },
        };
        _requestData = new RequestData(_frontendRequest, RequestMethod.GET);

        A.CallTo(() =>
                _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    "invalid-token",
                    A<CancellationToken>.Ignored
                )
            )
            .Returns((null, null));

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(_requestData, next);

        // Assert
        nextCalled.Should().BeFalse();
        _requestData.FrontendResponse!.StatusCode.Should().Be(401);
    }

    [Test]
    public async Task When_token_is_valid_but_missing_required_role_should_return_403()
    {
        // Arrange
        _frontendRequest = _frontendRequest with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer valid-token" },
        };
        _requestData = new RequestData(_frontendRequest, RequestMethod.GET);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, "test-user"),
            new Claim(ClaimTypes.Role, "wrong-role"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var clientAuthorizations = new ClientAuthorizations(
            TokenId: "token123",
            ClaimSetName: "test",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix>()
        );

        A.CallTo(() =>
                _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    "valid-token",
                    A<CancellationToken>.Ignored
                )
            )
            .Returns((principal, clientAuthorizations));

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(_requestData, next);

        // Assert
        nextCalled.Should().BeFalse();
        _requestData.FrontendResponse!.StatusCode.Should().Be(403);
        _requestData.FrontendResponse!.ContentType.Should().Be("application/problem+json");
    }

    [Test]
    public async Task When_token_is_valid_and_has_required_role_should_continue()
    {
        // Arrange
        _frontendRequest = _frontendRequest with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer valid-token" },
        };
        _requestData = new RequestData(_frontendRequest, RequestMethod.GET);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, "test-user"),
            new Claim(ClaimTypes.Role, "service"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var clientAuthorizations = new ClientAuthorizations(
            TokenId: "token123",
            ClaimSetName: "test",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix>()
        );

        A.CallTo(() =>
                _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    "valid-token",
                    A<CancellationToken>.Ignored
                )
            )
            .Returns((principal, clientAuthorizations));

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(_requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        _requestData.FrontendResponse.Should().Be(No.FrontendResponse);
    }

    [Test]
    public async Task When_no_role_configured_and_token_is_valid_should_continue()
    {
        // Arrange
        _options.ClientRole = string.Empty; // No role required
        _frontendRequest = _frontendRequest with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer valid-token" },
        };
        _requestData = new RequestData(_frontendRequest, RequestMethod.GET);

        var claims = new List<Claim> { new Claim(ClaimTypes.Name, "test-user") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var clientAuthorizations = new ClientAuthorizations(
            TokenId: "token123",
            ClaimSetName: "test",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix>()
        );

        A.CallTo(() =>
                _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    "valid-token",
                    A<CancellationToken>.Ignored
                )
            )
            .Returns((principal, clientAuthorizations));

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(_requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        _requestData.FrontendResponse.Should().Be(No.FrontendResponse);
    }
}
