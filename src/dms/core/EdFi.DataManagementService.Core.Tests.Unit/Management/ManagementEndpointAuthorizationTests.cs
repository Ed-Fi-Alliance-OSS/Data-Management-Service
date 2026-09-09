// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Management;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Management;

[TestFixture]
[Parallelizable]
public class Given_ManagementEndpointAuthorizationService
{
    private const string RequiredRole = "dms-management-operator";
    private const string RoleClaimType = "operator_role";
    private const string Token = "valid-token";

    private static ManagementEndpointAuthorizationService CreateService(
        ClaimsPrincipal? principal,
        string? requiredRole = RequiredRole,
        string roleClaimType = RoleClaimType,
        string clientRole = "legacy-service"
    )
    {
        var jwtValidationService = A.Fake<IJwtValidationService>();
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<(ClaimsPrincipal? Principal, ClientAuthorizations? ClientAuthorizations)>(
                    (principal, null)
                )
            );

        return new ManagementEndpointAuthorizationService(
            jwtValidationService,
            Options.Create(new ManagementEndpointsOptions { RequiredRole = requiredRole }),
            Options.Create(
                new JwtAuthenticationOptions { RoleClaimType = roleClaimType, ClientRole = clientRole }
            ),
            NullLogger<ManagementEndpointAuthorizationService>.Instance
        );
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Test]
    public async Task It_authorizes_an_exact_role_claim_under_the_configured_claim_type()
    {
        ManagementEndpointAuthorizationService service = CreateService(
            Principal(new Claim(RoleClaimType, RequiredRole))
        );

        EndpointRoleAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(EndpointRoleAuthorizationOutcome.Authorized);
        result.IsAuthorized.Should().BeTrue();
    }

    [Test]
    public async Task It_returns_unauthorized_when_the_header_is_absent()
    {
        ManagementEndpointAuthorizationService service = CreateService(
            Principal(new Claim(RoleClaimType, RequiredRole))
        );

        EndpointRoleAuthorizationResult result = await service.AuthorizeAsync(null);

        result.Outcome.Should().Be(EndpointRoleAuthorizationOutcome.Unauthorized);
        result.Message.Should().Be("Authorization header is missing.");
    }

    [Test]
    public async Task It_returns_forbidden_when_the_role_claim_uses_a_different_claim_type()
    {
        ManagementEndpointAuthorizationService service = CreateService(
            Principal(new Claim(ClaimTypes.Role, RequiredRole), new Claim("roles", RequiredRole))
        );

        EndpointRoleAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(EndpointRoleAuthorizationOutcome.Forbidden);
        result.Message.Should().Be("Insufficient permissions");
    }

    [Test]
    public async Task It_does_not_fall_back_to_the_configured_client_role()
    {
        ManagementEndpointAuthorizationService service = CreateService(
            Principal(new Claim(RoleClaimType, "dms-client")),
            clientRole: "dms-client"
        );

        EndpointRoleAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(EndpointRoleAuthorizationOutcome.Forbidden);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("dms management operator")]
    public async Task It_reports_the_required_role_as_not_configured(string? requiredRole)
    {
        ManagementEndpointAuthorizationService service = CreateService(
            Principal(new Claim(RoleClaimType, RequiredRole)),
            requiredRole: requiredRole
        );

        EndpointRoleAuthorizationResult result = await service.AuthorizeAsync($"Bearer {Token}");

        result.Outcome.Should().Be(EndpointRoleAuthorizationOutcome.RequiredRoleNotConfigured);
    }
}

[TestFixture]
[Parallelizable]
public class Given_ManagementEndpointsOptions
{
    [Test]
    public void It_yields_a_valid_required_role()
    {
        ManagementEndpointsOptions options = new() { RequiredRole = "dms-management-operator" };

        options.TryGetRequiredRoleForEndpointMapping(out string? requiredRole).Should().BeTrue();
        requiredRole.Should().Be("dms-management-operator");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("dms management operator")]
    public void It_rejects_an_unusable_required_role(string? configuredRole)
    {
        ManagementEndpointsOptions options = new() { RequiredRole = configuredRole };

        options.TryGetRequiredRoleForEndpointMapping(out string? requiredRole).Should().BeFalse();
        requiredRole.Should().BeNull();
    }

    [Test]
    public void It_omits_the_required_role_when_options_are_serialized()
    {
        ManagementEndpointsOptions options = new() { RequiredRole = "dms-management-operator" };

        string json = System.Text.Json.JsonSerializer.Serialize(options);

        json.Should().NotContain("RequiredRole");
        json.Should().NotContain("dms-management-operator");
    }

    [Test]
    public void It_publishes_the_configuration_section_name()
    {
        ManagementEndpointsOptions.SectionName.Should().Be("AppSettings:ManagementEndpoints");
    }
}
