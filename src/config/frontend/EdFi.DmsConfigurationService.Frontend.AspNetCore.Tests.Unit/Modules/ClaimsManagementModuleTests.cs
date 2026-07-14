// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
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
/// (SecurityConstants.ServicePolicy plus an AuthorizationScopePolicies scope) in addition to
/// the existing DangerouslyEnableUnrestrictedClaimsLoading flag check.
/// </summary>
public abstract class ClaimsManagementModuleTests
{
    protected const string ReloadClaimsRoute = "/management/reload-claims";
    protected const string UploadClaimsRoute = "/management/upload-claims";
    protected const string CurrentClaimsRoute = "/management/current-claims";

    // A role the TestAuthHandler principal never carries; used to prove ServicePolicy is enforced.
    protected const string RoleTheTokenDoesNotHave = "unassigned-configuration-service-role";

    // A syntactically invalid (non-JWT) bearer value that the production JWT handler rejects.
    protected const string InvalidBearerToken = "not-a-valid-jwt";

    private readonly IClaimsUploadService _claimsUploadService = A.Fake<IClaimsUploadService>();
    private readonly IClaimsProvider _claimsProvider = A.Fake<IClaimsProvider>();

    private WebApplicationFactory<Program> _factory = null!;
    protected HttpClient Client = null!;

    [TearDown]
    public void DisposeClientAndFactory()
    {
        Client?.Dispose();
        _factory?.Dispose();
    }

    protected static StringContent EmptyJsonBody() => new("{}", Encoding.UTF8, "application/json");

    /// <summary>
    /// Asserts that a response carries the full Ed-Fi not-found Problem Details contract
    /// (application/problem+json, type urn:ed-fi:api:not-found, non-empty correlationId, and the
    /// empty validationErrors/errors defaults) rather than a bare framework 404.
    /// </summary>
    protected static async Task AssertNotFoundContract(HttpResponseMessage response, string expectedDetail)
    {
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        var actual = JsonNode.Parse(content);
        actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var expected = JsonNode.Parse(
            """
            {
              "detail": "{detail}",
              "type": "urn:ed-fi:api:not-found",
              "title": "Not Found",
              "status": 404,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": []
            }
            """.Replace("{detail}", expectedDetail).Replace(
                "{correlationId}",
                actual!["correlationId"]!.GetValue<string>()
            )
        );
        JsonNode.DeepEquals(actual, expected).Should().Be(true);
    }

    protected void ArrangeUnauthenticatedClient(bool dangerousFlagEnabled)
    {
        _factory = CreateFactory(
            addTestAuthentication: false,
            dangerousFlagEnabled,
            requiredServiceRole: null
        );
        Client = _factory.CreateClient();
    }

    protected void ArrangeClientWithInvalidBearerToken(bool dangerousFlagEnabled)
    {
        _factory = CreateFactory(
            addTestAuthentication: false,
            dangerousFlagEnabled,
            requiredServiceRole: null
        );
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            InvalidBearerToken
        );
    }

    protected void ArrangeAuthenticatedClient(
        string scope,
        bool dangerousFlagEnabled,
        string? requiredServiceRole = null
    )
    {
        _factory = CreateFactory(addTestAuthentication: true, dangerousFlagEnabled, requiredServiceRole);
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
    }

    private WebApplicationFactory<Program> CreateFactory(
        bool addTestAuthentication,
        bool dangerousFlagEnabled,
        string? requiredServiceRole
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                            // requiredServiceRole lets a test require a role the principal lacks,
                            // proving the route enforces ServicePolicy (patterned after ActionModuleTests).
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy =>
                                    policy.RequireClaim(
                                        identitySettings.RoleClaimType,
                                        requiredServiceRole ?? identitySettings.ConfigServiceRole
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
    }

    /// <summary>
    /// Authentication is evaluated before the dangerous-flag check, so a request without a token
    /// returns 401 even when the flag is enabled.
    /// </summary>
    [TestFixture]
    public class Given_no_bearer_token_and_the_dangerous_flag_is_enabled : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() => ArrangeUnauthenticatedClient(dangerousFlagEnabled: true);

        [Test]
        public async Task It_should_reject_reload_claims_with_401()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_upload_claims_with_401()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_current_claims_with_401()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    /// <summary>
    /// Authentication is evaluated before the dangerous-flag check, so a request without a token
    /// returns 401 even when the flag is disabled.
    /// </summary>
    [TestFixture]
    public class Given_no_bearer_token_and_the_dangerous_flag_is_disabled : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() => ArrangeUnauthenticatedClient(dangerousFlagEnabled: false);

        [Test]
        public async Task It_should_reject_reload_claims_with_401()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_upload_claims_with_401()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_current_claims_with_401()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    /// <summary>
    /// A malformed (non-JWT) bearer token is rejected by the production JWT handler with 401
    /// before the dangerous-flag check, even when the flag is enabled.
    /// </summary>
    [TestFixture]
    public class Given_an_invalid_bearer_token_and_the_dangerous_flag_is_enabled : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() => ArrangeClientWithInvalidBearerToken(dangerousFlagEnabled: true);

        [Test]
        public async Task It_should_reject_reload_claims_with_401()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_upload_claims_with_401()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_current_claims_with_401()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    /// <summary>
    /// A malformed (non-JWT) bearer token is rejected by the production JWT handler with 401
    /// before the dangerous-flag check, even when the flag is disabled.
    /// </summary>
    [TestFixture]
    public class Given_an_invalid_bearer_token_and_the_dangerous_flag_is_disabled
        : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() => ArrangeClientWithInvalidBearerToken(dangerousFlagEnabled: false);

        [Test]
        public async Task It_should_reject_reload_claims_with_401()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_upload_claims_with_401()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task It_should_reject_current_claims_with_401()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    /// <summary>
    /// A read-only token lacks the admin scope required by the write endpoints (403) but is
    /// accepted by the read endpoint's ReadOnlyOrAdmin policy.
    /// </summary>
    [TestFixture]
    public class Given_a_read_only_token : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.ReadOnlyScope.Name, dangerousFlagEnabled: false);

        [Test]
        public async Task It_should_reject_reload_claims_with_403()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task It_should_reject_upload_claims_with_403()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task It_should_authorize_current_claims_and_return_404_when_flag_disabled()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// A fully authorized request still returns 404 while the dangerous flag is disabled, proving
    /// authorization does not bypass the flag gate.
    /// </summary>
    [TestFixture]
    public class Given_a_full_access_token_and_the_dangerous_flag_is_disabled : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: false);

        [Test]
        public async Task It_should_return_404_for_reload_claims()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_should_return_404_for_upload_claims()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_should_return_404_for_current_claims()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// An authenticated principal that carries an allowed scope but fails the configuration-service
    /// role requirement must be rejected with 403 on every endpoint, proving each route enforces
    /// ServicePolicy. The dangerous flag is disabled so a route missing ServicePolicy would instead
    /// reach the handler and return 404, which the 403 assertion distinguishes.
    /// </summary>
    [TestFixture]
    public class Given_a_token_without_the_configuration_service_role : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AdminScope.Name,
                dangerousFlagEnabled: false,
                requiredServiceRole: RoleTheTokenDoesNotHave
            );

        [Test]
        public async Task It_should_reject_reload_claims_with_403()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task It_should_reject_upload_claims_with_403()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task It_should_reject_current_claims_with_403()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    /// <summary>
    /// The read endpoint uses MapSecuredGet (ReadOnlyOrAdmin), not the broader MapLimitedAccess
    /// policy, so a valid token whose only scope is the auth-metadata read-only scope must be
    /// rejected with 403.
    /// </summary>
    [TestFixture]
    public class Given_a_token_with_an_unsupported_scope_for_the_read_endpoint : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name,
                dangerousFlagEnabled: false
            );

        [Test]
        public async Task It_should_reject_current_claims_with_403()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    /// <summary>
    /// With a fully authorized request and the dangerous flag disabled, each management endpoint
    /// returns a compliant Ed-Fi not-found Problem Details response, not a bare framework 404.
    /// </summary>
    [TestFixture]
    public class Given_a_full_access_token_and_the_flag_disabled_the_disabled_response_is_compliant
        : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: false);

        [Test]
        public async Task It_returns_a_compliant_not_found_for_reload_claims()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            await AssertNotFoundContract(response, "Claims reload endpoint is not available.");
        }

        [Test]
        public async Task It_returns_a_compliant_not_found_for_upload_claims()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            await AssertNotFoundContract(response, "Claims upload endpoint is not available.");
        }

        [Test]
        public async Task It_returns_a_compliant_not_found_for_current_claims()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            await AssertNotFoundContract(response, "Current claims endpoint is not available.");
        }
    }
}
