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

/// <summary>
/// A signed token with a scope array becomes one <c>scope</c> claim per element, so the handler must consider every
/// scope claim, and split each by ASCII space, whatever position the allowed scope has.
/// </summary>
[TestFixture]
internal class Given_ScopePolicy_With_Multiple_Claims
{
    private const string Required = "required_scope";

    private readonly Dictionary<string, bool> _outcomes = [];

    private static async Task<bool> EvaluateAsync(string[] allowedScopes, params string[] scopeClaimValues)
    {
        ScopePolicy requirement = new(allowedScopes);
        ClaimsPrincipal user = new(
            new ClaimsIdentity(scopeClaimValues.Select(value => new Claim("scope", value)), "TestAuthType")
        );
        AuthorizationHandlerContext context = new([requirement], user, null);

        await ((IAuthorizationHandler)new ScopePolicyHandler()).HandleAsync(context);

        return context.HasSucceeded;
    }

    [SetUp]
    public async Task Setup()
    {
        string[] required = [Required];
        _outcomes["required last"] = await EvaluateAsync(required, "other_scope", Required);
        _outcomes["required middle"] = await EvaluateAsync(required, "first_scope", Required, "last_scope");
        _outcomes["required first"] = await EvaluateAsync(required, Required, "other_scope");
        _outcomes["required last in a list element"] = await EvaluateAsync(
            required,
            "other_scope",
            $"another_scope {Required}"
        );
        _outcomes["required last after repeated spaces"] = await EvaluateAsync(
            required,
            $"other_scope   {Required}"
        );
        _outcomes["second of several allowed, last claim"] = await EvaluateAsync(
            ["scope_one", "scope_two"],
            "other_scope",
            "scope_two"
        );
        _outcomes["no claim matches"] = await EvaluateAsync(required, "other_scope", "another_scope");
        _outcomes["case differs"] = await EvaluateAsync(required, "other_scope", Required.ToUpperInvariant());
        _outcomes["prefix only"] = await EvaluateAsync(required, "other_scope", "required");
        _outcomes["tab is not a separator"] = await EvaluateAsync(required, $"other_scope\t{Required}");
        _outcomes["empty claims only"] = await EvaluateAsync(required, "", "   ");
    }

    [Test]
    public void It_succeeds_when_the_required_scope_is_the_last_claim() =>
        _outcomes["required last"].Should().BeTrue();

    [Test]
    public void It_succeeds_when_the_required_scope_is_a_middle_claim() =>
        _outcomes["required middle"].Should().BeTrue();

    [Test]
    public void It_succeeds_when_the_required_scope_is_the_first_claim() =>
        _outcomes["required first"].Should().BeTrue();

    [Test]
    public void It_succeeds_when_the_required_scope_ends_a_space_separated_claim() =>
        _outcomes["required last in a list element"].Should().BeTrue();

    [Test]
    public void It_ignores_repeated_spaces_between_scopes() =>
        _outcomes["required last after repeated spaces"].Should().BeTrue();

    [Test]
    public void It_succeeds_for_any_allowed_scope_in_a_later_claim() =>
        _outcomes["second of several allowed, last claim"].Should().BeTrue();

    [Test]
    public void It_does_not_succeed_when_no_claim_matches() =>
        _outcomes["no claim matches"].Should().BeFalse();

    [Test]
    public void It_compares_scopes_case_sensitively() => _outcomes["case differs"].Should().BeFalse();

    [Test]
    public void It_does_not_match_a_prefix_of_the_required_scope() =>
        _outcomes["prefix only"].Should().BeFalse();

    [Test]
    public void It_splits_only_on_ascii_space() => _outcomes["tab is not a separator"].Should().BeFalse();

    [Test]
    public void It_does_not_succeed_on_empty_claims() => _outcomes["empty claims only"].Should().BeFalse();
}

/// <summary>
/// The scope claim type is matched exactly and case-sensitively: a claim typed <c>SCOPE</c> or <c>Scope</c> is not a
/// scope claim, even though the BCL claim lookup by type is case-insensitive.
/// </summary>
[TestFixture]
internal class Given_ScopePolicy_With_Differently_Cased_Claim_Types
{
    private const string Required = "required_scope";

    private readonly Dictionary<string, bool> _outcomes = [];

    private static async Task<bool> EvaluateAsync(params Claim[] claims)
    {
        ScopePolicy requirement = new(Required);
        ClaimsPrincipal user = new(new ClaimsIdentity(claims, "TestAuthType"));
        AuthorizationHandlerContext context = new([requirement], user, null);

        await ((IAuthorizationHandler)new ScopePolicyHandler()).HandleAsync(context);

        return context.HasSucceeded;
    }

    [SetUp]
    public async Task Setup()
    {
        _outcomes["upper-case type"] = await EvaluateAsync(
            new Claim("role", "service"),
            new Claim("SCOPE", Required)
        );
        _outcomes["title-case type"] = await EvaluateAsync(
            new Claim("role", "service"),
            new Claim("Scope", Required)
        );
        _outcomes["exact type"] = await EvaluateAsync(
            new Claim("role", "service"),
            new Claim("scope", Required)
        );
        _outcomes["exact type alongside a cased type"] = await EvaluateAsync(
            new Claim("SCOPE", Required),
            new Claim("scope", "other_scope")
        );
    }

    [Test]
    public void It_does_not_treat_an_upper_case_type_as_a_scope_claim() =>
        _outcomes["upper-case type"].Should().BeFalse();

    [Test]
    public void It_does_not_treat_a_title_case_type_as_a_scope_claim() =>
        _outcomes["title-case type"].Should().BeFalse();

    [Test]
    public void It_succeeds_for_a_claim_with_the_exact_scope_type() =>
        _outcomes["exact type"].Should().BeTrue();

    [Test]
    public void It_ignores_a_cased_type_even_when_an_exact_scope_claim_is_present() =>
        _outcomes["exact type alongside a cased type"].Should().BeFalse();
}
