// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Security.Claims;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.Authorization;

[TestFixture]
internal class ScopePolicyHandlerTests
{
    private ScopePolicyHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new ScopePolicyHandler();
    }

    private async Task InvokeHandlerAsync(AuthorizationHandlerContext context, ScopePolicy requirement)
    {
        var method = typeof(ScopePolicyHandler).GetMethod(
            "HandleRequirementAsync",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        var task = method?.Invoke(_handler, [context, requirement]) as Task;
        if (task != null)
        {
            await task;
        }
    }

    [Test]
    public async Task When_user_has_no_scope_claim_does_not_succeed()
    {
        // Arrange
        Claim[] claims = [new Claim("role", "admin")];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        string[] scopes = ["required_scope"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeFalse();
    }

    [Test]
    public async Task When_user_has_scope_claim_but_no_matching_scope_does_not_succeed()
    {
        // Arrange
        Claim[] claims = [new Claim("scope", "other_scope another_scope")];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        string[] scopes = ["required_scope"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeFalse();
    }

    [Test]
    public async Task When_user_has_matching_scope_succeeds()
    {
        // Arrange
        Claim[] claims = [new Claim("scope", "required_scope other_scope")];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        string[] scopes = ["required_scope"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeTrue();
    }

    [Test]
    public async Task When_user_has_one_of_multiple_allowed_scopes_succeeds()
    {
        // Arrange
        Claim[] claims = [new Claim("scope", "scope_two")];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        string[] scopes = ["scope_one", "scope_two", "scope_three"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeTrue();
    }

    [Test]
    public async Task When_user_has_multiple_scopes_and_one_matches_succeeds()
    {
        // Arrange
        Claim[] claims = [new Claim("scope", "scope_one scope_two scope_three")];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        string[] scopes = ["scope_two"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeTrue();
    }

    [Test]
    public async Task When_user_has_empty_scope_claim_does_not_succeed()
    {
        // Arrange
        Claim[] claims = [new Claim("scope", "")];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        string[] scopes = ["required_scope"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeFalse();
    }

    [Test]
    public async Task When_user_has_whitespace_only_scope_claim_does_not_succeed()
    {
        // Arrange
        Claim[] claims = [new Claim("scope", "   ")];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        string[] scopes = ["required_scope"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeFalse();
    }

    [Test]
    public async Task When_user_is_unauthenticated_does_not_succeed()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity()); // No authentication type
        string[] scopes = ["required_scope"];
        var requirement = new ScopePolicy(scopes);
        IAuthorizationRequirement[] requirements = [requirement];
        var context = new AuthorizationHandlerContext(requirements, user, null);

        // Act
        await InvokeHandlerAsync(context, requirement);

        // Assert
        context.HasSucceeded.Should().BeFalse();
    }
}
