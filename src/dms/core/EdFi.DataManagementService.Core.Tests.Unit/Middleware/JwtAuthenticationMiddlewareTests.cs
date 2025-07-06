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
public class JwtAuthenticationMiddlewareTests
{
    private IJwtValidationService _jwtValidationService = null!;
    private ILogger<JwtAuthenticationMiddleware> _logger = null!;
    private IOptions<JwtAuthenticationOptions> _options = null!;
    private JwtAuthenticationMiddleware _middleware = null!;

    [SetUp]
    public void Setup()
    {
        _jwtValidationService = A.Fake<IJwtValidationService>();
        _logger = A.Fake<ILogger<JwtAuthenticationMiddleware>>();
        _options = A.Fake<IOptions<JwtAuthenticationOptions>>();

        var jwtOptions = new JwtAuthenticationOptions { Enabled = true };
        A.CallTo(() => _options.Value).Returns(jwtOptions);

        _middleware = new JwtAuthenticationMiddleware(_jwtValidationService, _logger, _options);
    }

    [Test]
    public async Task Execute_WhenDisabled_CallsNext()
    {
        // Arrange
        var disabledOptions = new JwtAuthenticationOptions { Enabled = false };
        A.CallTo(() => _options.Value).Returns(disabledOptions);
        _middleware = new JwtAuthenticationMiddleware(_jwtValidationService, _logger, _options);

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string>(),
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("123")
        );
        var requestData = new RequestData(frontendRequest, RequestMethod.GET);

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        requestData.FrontendResponse.Should().Be(No.FrontendResponse);
    }

    [Test]
    public async Task Execute_WithValidToken_SetsClientAuthorizations()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer valid-token" },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("123")
        );
        var requestData = new RequestData(frontendRequest, RequestMethod.GET);

        var expectedPrincipal = new ClaimsPrincipal();
        var expectedAuthorizations = new ClientAuthorizations(
            TokenId: "token-123",
            ClaimSetName: "edfi-admin",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix>()
        );

        A.CallTo(() =>
                _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    "valid-token",
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                    (expectedPrincipal, expectedAuthorizations)
                )
            );

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue();
        requestData.ClientAuthorizations.Should().Be(expectedAuthorizations);
        requestData.FrontendResponse.Should().Be(No.FrontendResponse);
    }

    [Test]
    public async Task Execute_WithInvalidToken_Returns401()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer invalid-token" },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("123")
        );
        var requestData = new RequestData(frontendRequest, RequestMethod.GET);

        A.CallTo(() =>
                _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    "invalid-token",
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>((null, null)));

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse();
        requestData.FrontendResponse.StatusCode.Should().Be(401);
        requestData.FrontendResponse.Headers.Should().ContainKey("WWW-Authenticate");
        requestData.FrontendResponse.ContentType.Should().Be("application/problem+json");
    }

    [Test]
    public async Task Execute_WithMissingAuthorizationHeader_Returns401()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string>(),
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("123")
        );
        var requestData = new RequestData(frontendRequest, RequestMethod.GET);

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse();
        requestData.FrontendResponse.StatusCode.Should().Be(401);
    }

    [Test]
    public async Task Execute_WithNonBearerToken_Returns401()
    {
        // Arrange
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string> { ["Authorization"] = "Basic dXNlcjpwYXNz" },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("123")
        );
        var requestData = new RequestData(frontendRequest, RequestMethod.GET);

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeFalse();
        requestData.FrontendResponse.StatusCode.Should().Be(401);
    }

    [Test]
    public async Task Execute_WithClientSpecificRollout_OnlyProcessesEnabledClients()
    {
        // Arrange
        var rolloutOptions = new JwtAuthenticationOptions
        {
            Enabled = true,
            EnabledForClients = new List<string> { "allowed-client" },
        };
        A.CallTo(() => _options.Value).Returns(rolloutOptions);
        _middleware = new JwtAuthenticationMiddleware(_jwtValidationService, _logger, _options);

        // Request from non-allowed client (should bypass JWT validation)
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Headers: new Dictionary<string, string>(),
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId("123")
        );
        var requestData = new RequestData(frontendRequest, RequestMethod.GET);
        requestData.ClientAuthorizations = new ClientAuthorizations(
            TokenId: "not-allowed-client",
            ClaimSetName: "edfi-admin",
            EducationOrganizationIds: new List<EducationOrganizationId>(),
            NamespacePrefixes: new List<NamespacePrefix>()
        );

        var nextCalled = false;
        Func<Task> next = () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await _middleware.Execute(requestData, next);

        // Assert
        nextCalled.Should().BeTrue(); // Should bypass validation for non-allowed client
        requestData.FrontendResponse.Should().Be(No.FrontendResponse);
    }
}
