// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// Verifies that the /management/* claims endpoints require CMS bearer authorization
/// (ServicePolicy + an AuthorizationScopePolicies scope) in addition to the existing
/// DangerouslyEnableUnrestrictedClaimsLoading flag check.
/// </summary>
[TestFixture]
public class ClaimsManagementModuleTests
{
    private const string ReloadClaimsRoute = "/management/reload-claims";
    private const string UploadClaimsRoute = "/management/upload-claims";
    private const string CurrentClaimsRoute = "/management/current-claims";

    private readonly IClaimsUploadService _claimsUploadService = A.Fake<IClaimsUploadService>();
    private readonly IClaimsProvider _claimsProvider = A.Fake<IClaimsProvider>();
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        _factories.Clear();
    }

    private WebApplicationFactory<Program> CreateFactory(
        bool addTestAuthentication,
        bool dangerousFlagEnabled
    )
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
                    if (addTestAuthentication)
                    {
                        // Mimic the production authentication/authorization setup so that
                        // ServicePolicy and the scope policies are evaluated for each request.
                        collection.AddTestAuthentication();

                        var identitySettings = ctx
                            .Configuration.GetSection("IdentitySettings")
                            .Get<IdentitySettings>()!;
                        collection.AddAuthorization(options =>
                        {
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy =>
                                    policy.RequireClaim(
                                        identitySettings.RoleClaimType,
                                        identitySettings.ConfigServiceRole
                                    )
                            );
                            AuthorizationScopePolicies.Add(options);
                        });

                        collection.AddTransient(_ => _claimsUploadService);
                        collection.AddTransient(_ => _claimsProvider);
                    }

                    // Force the dangerous flag so the handler's inner gate is deterministic.
                    collection.Configure<ClaimsOptions>(options =>
                        options.DangerouslyEnableUnrestrictedClaimsLoading = dangerousFlagEnabled
                    );
                }
            );
        });

        _factories.Add(factory);
        return factory;
    }

    private HttpClient CreateUnauthenticatedClient(bool dangerousFlagEnabled) =>
        CreateFactory(addTestAuthentication: false, dangerousFlagEnabled).CreateClient();

    private HttpClient CreateAuthenticatedClient(string scope, bool dangerousFlagEnabled)
    {
        var client = CreateFactory(addTestAuthentication: true, dangerousFlagEnabled).CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        return client;
    }

    private static StringContent EmptyJsonBody() => new("{}", Encoding.UTF8, "application/json");

    // Authentication is evaluated before the dangerous-flag check, so a request without a token
    // returns 401 whether the flag is enabled or disabled.

    [TestCase(true)]
    [TestCase(false)]
    public async Task Reload_claims_without_a_token_returns_401(bool dangerousFlagEnabled)
    {
        using var client = CreateUnauthenticatedClient(dangerousFlagEnabled);

        var response = await client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Upload_claims_without_a_token_returns_401(bool dangerousFlagEnabled)
    {
        using var client = CreateUnauthenticatedClient(dangerousFlagEnabled);

        var response = await client.PostAsync(UploadClaimsRoute, EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Current_claims_without_a_token_returns_401(bool dangerousFlagEnabled)
    {
        using var client = CreateUnauthenticatedClient(dangerousFlagEnabled);

        var response = await client.GetAsync(CurrentClaimsRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // A valid CMS token with an insufficient scope (read-only) cannot call the write endpoints.

    [Test]
    public async Task Reload_claims_with_a_read_only_token_returns_403()
    {
        using var client = CreateAuthenticatedClient(
            AuthorizationScopes.ReadOnlyScope.Name,
            dangerousFlagEnabled: false
        );

        var response = await client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Upload_claims_with_a_read_only_token_returns_403()
    {
        using var client = CreateAuthenticatedClient(
            AuthorizationScopes.ReadOnlyScope.Name,
            dangerousFlagEnabled: false
        );

        var response = await client.PostAsync(UploadClaimsRoute, EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // A read-only token is authorized for the read endpoint (not 403); with the flag disabled
    // the handler still returns 404.

    [Test]
    public async Task Current_claims_with_a_read_only_token_is_authorized_and_returns_404_when_flag_disabled()
    {
        using var client = CreateAuthenticatedClient(
            AuthorizationScopes.ReadOnlyScope.Name,
            dangerousFlagEnabled: false
        );

        var response = await client.GetAsync(CurrentClaimsRoute);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // A full-access token is authorized; with the flag disabled every endpoint still returns 404.

    [Test]
    public async Task Reload_claims_with_a_full_access_token_returns_404_when_flag_disabled()
    {
        using var client = CreateAuthenticatedClient(
            AuthorizationScopes.AdminScope.Name,
            dangerousFlagEnabled: false
        );

        var response = await client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Upload_claims_with_a_full_access_token_returns_404_when_flag_disabled()
    {
        using var client = CreateAuthenticatedClient(
            AuthorizationScopes.AdminScope.Name,
            dangerousFlagEnabled: false
        );

        var response = await client.PostAsync(UploadClaimsRoute, EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Current_claims_with_a_full_access_token_returns_404_when_flag_disabled()
    {
        using var client = CreateAuthenticatedClient(
            AuthorizationScopes.AdminScope.Name,
            dangerousFlagEnabled: false
        );

        var response = await client.GetAsync(CurrentClaimsRoute);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
