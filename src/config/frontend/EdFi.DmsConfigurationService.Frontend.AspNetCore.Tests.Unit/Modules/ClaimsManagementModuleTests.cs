// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
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

    // The framework-401 errors message when no credential was supplied versus one that was supplied
    // but rejected. The detail is identical; only these differ.
    protected const string MissingCredentialError = "Authentication is required to access this resource.";
    protected const string InvalidCredentialError =
        "The supplied authentication credentials were invalid or have expired.";

    protected readonly IClaimsUploadService ClaimsUploadService = A.Fake<IClaimsUploadService>();
    protected readonly IClaimsProvider ClaimsProvider = A.Fake<IClaimsProvider>();

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

    /// <summary>
    /// Asserts the full Ed-Fi internal-server-error contract and that the response is not the old
    /// ad hoc { error, message } shape and does not leak an exception message.
    /// </summary>
    protected static async Task AssertInternalServerErrorContract(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        var actual = JsonNode.Parse(content);
        actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        actual["error"].Should().BeNull();
        actual["message"].Should().BeNull();

        var expected = JsonNode.Parse(
            """
            {
              "detail": "",
              "type": "urn:ed-fi:api:internal-server-error",
              "title": "Internal Server Error",
              "status": 500,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": []
            }
            """.Replace("{correlationId}", actual!["correlationId"]!.GetValue<string>())
        );
        JsonNode.DeepEquals(actual, expected).Should().Be(true);
    }

    /// <summary>
    /// Asserts the full Ed-Fi generic bad-request contract for non-data-validation 400s.
    /// </summary>
    protected static async Task AssertBadRequestContract(HttpResponseMessage response, string expectedDetail)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        var actual = JsonNode.Parse(content);
        actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var expected = JsonNode.Parse(
            """
            {
              "detail": "{detail}",
              "type": "urn:ed-fi:api:bad-request",
              "title": "Bad Request",
              "status": 400,
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

    /// <summary>
    /// Asserts the shared fields of the Ed-Fi data-validation contract and returns the parsed body so
    /// the caller can assert the grouped validationErrors.
    /// </summary>
    protected static async Task<JsonNode> AssertDataValidationContract(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        var actual = JsonNode.Parse(content)!;
        actual["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
        actual["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
        actual["title"]!.GetValue<string>().Should().Be("Data Validation Failed");
        actual["status"]!.GetValue<int>().Should().Be(400);
        actual["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        actual["errors"]!.AsArray().Count.Should().Be(0);
        return actual;
    }

    /// <summary>
    /// Asserts the full Ed-Fi authentication contract for framework-generated 401 responses.
    /// </summary>
    protected static async Task AssertUnauthorizedContract(HttpResponseMessage response, string expectedError)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        // The configured authentication scheme's challenge (WWW-Authenticate) must be preserved.
        response.Headers.WwwAuthenticate.Select(header => header.Scheme).Should().Contain("Bearer");

        string content = await response.Content.ReadAsStringAsync();
        var actual = JsonNode.Parse(content);
        actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var expected = JsonNode.Parse(
            """
            {
              "detail": "The caller could not be authenticated.",
              "type": "urn:ed-fi:api:security:authentication",
              "title": "Authentication Failed",
              "status": 401,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": ["{error}"]
            }
            """.Replace("{correlationId}", actual!["correlationId"]!.GetValue<string>()).Replace(
                "{error}",
                expectedError
            )
        );
        JsonNode.DeepEquals(actual, expected).Should().Be(true);
    }

    /// <summary>
    /// Asserts the full Ed-Fi authorization contract for framework-generated 403 responses.
    /// </summary>
    protected static async Task AssertForbiddenContract(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        var actual = JsonNode.Parse(content);
        actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var expected = JsonNode.Parse(
            """
            {
              "detail": "Access to the resource could not be authorized.",
              "type": "urn:ed-fi:api:security:authorization",
              "title": "Authorization Denied",
              "status": 403,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": ["Access to this resource is forbidden."]
            }
            """.Replace("{correlationId}", actual!["correlationId"]!.GetValue<string>())
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

                        collection.AddTransient(_ => ClaimsUploadService);
                        collection.AddTransient(_ => ClaimsProvider);
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

    /// <summary>
    /// Findings 5 and 6: with the flag enabled and an authorized request, current-claims exceptions
    /// return a compliant Ed-Fi 500 rather than ad hoc { error, message } JSON that leaks the message.
    /// </summary>
    [TestFixture]
    public class Given_a_full_access_token_the_flag_enabled_and_current_claims_throws
        : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_a_compliant_500_on_json_exception()
        {
            A.CallTo(() => ClaimsProvider.GetClaimsDocumentNodes())
                .Throws(new JsonException("secret parse detail"));

            var response = await Client.GetAsync(CurrentClaimsRoute);

            await AssertInternalServerErrorContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_500_on_invalid_operation()
        {
            A.CallTo(() => ClaimsProvider.GetClaimsDocumentNodes())
                .Throws(new InvalidOperationException("secret operation detail"));

            var response = await Client.GetAsync(CurrentClaimsRoute);

            await AssertInternalServerErrorContract(response);
        }
    }

    /// <summary>
    /// With the flag enabled and an authorized request, reload-claims failure and exception branches
    /// return the compliant Ed-Fi contract instead of custom response envelopes.
    /// </summary>
    [TestFixture]
    public class Given_a_full_access_token_and_the_flag_enabled_for_reload_claims
        : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_a_compliant_500_when_reload_reports_failures()
        {
            A.CallTo(() => ClaimsUploadService.ReloadClaimsAsync())
                .Returns(new ClaimsLoadStatus(false, [new ClaimsFailure("LoadError", "source unavailable")]));

            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

            await AssertInternalServerErrorContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_400_on_json_exception()
        {
            A.CallTo(() => ClaimsUploadService.ReloadClaimsAsync())
                .Throws(new JsonException("secret parse detail"));

            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

            await AssertBadRequestContract(response, "The claims source could not be parsed as valid JSON.");
        }

        [Test]
        public async Task It_returns_a_compliant_500_on_invalid_operation()
        {
            A.CallTo(() => ClaimsUploadService.ReloadClaimsAsync())
                .Throws(new InvalidOperationException("secret operation detail"));

            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

            await AssertInternalServerErrorContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_bad_request_400_when_reload_reports_a_json_error_failure()
        {
            // The real malformed-source flow: the service catches the JSON error internally and reports
            // it as a JsonError failure rather than throwing.
            A.CallTo(() => ClaimsUploadService.ReloadClaimsAsync())
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [
                            new ClaimsFailure(
                                "JsonError",
                                "Invalid JSON format encountered during claims reload"
                            ),
                        ]
                    )
                );

            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

            await AssertBadRequestContract(response, "The claims source was invalid.");
        }

        [Test]
        public async Task It_returns_a_compliant_data_validation_400_when_reload_reports_validation_failures()
        {
            A.CallTo(() => ClaimsUploadService.ReloadClaimsAsync())
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [
                            new ClaimsFailure(
                                "Validation",
                                "Claim set name is required.",
                                "$.claimSets[0].claimSetName"
                            ),
                        ]
                    )
                );

            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());

            var body = await AssertDataValidationContract(response);
            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors["$.claimSets[0].claimSetName"]!.AsArray()[0]!
                .GetValue<string>()
                .Should()
                .Be("Claim set name is required.");
        }
    }

    /// <summary>
    /// With the flag enabled and an authorized request, upload-claims error branches return the
    /// compliant Ed-Fi contract: data-validation for missing/invalid claims, generic bad-request for
    /// malformed input, and internal-server-error for unexpected operations.
    /// </summary>
    [TestFixture]
    public class Given_a_full_access_token_and_the_flag_enabled_for_upload_claims
        : ClaimsManagementModuleTests
    {
        private static StringContent ClaimsBody() =>
            new("""{ "claims": { "claimSets": [] } }""", Encoding.UTF8, "application/json");

        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_a_compliant_data_validation_400_when_claims_are_missing()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());

            var body = await AssertDataValidationContract(response);
            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors.Count.Should().Be(1);
            validationErrors["Claims"]!.AsArray()[0]!
                .GetValue<string>()
                .Should()
                .Be("Claims JSON is required.");
        }

        [Test]
        public async Task It_returns_a_compliant_data_validation_400_for_validation_failures()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [
                            new ClaimsFailure(
                                "Validation",
                                "Claim set name is required.",
                                "$.claimSets[0].claimSetName"
                            ),
                        ]
                    )
                );

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            var body = await AssertDataValidationContract(response);
            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors["$.claimSets[0].claimSetName"]!.AsArray()[0]!
                .GetValue<string>()
                .Should()
                .Be("Claim set name is required.");
        }

        [Test]
        public async Task It_returns_a_compliant_data_validation_400_for_structure_failures()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [new ClaimsFailure("Structure", "Missing required 'claimSets' property")]
                    )
                );

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            var body = await AssertDataValidationContract(response);
            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors["Structure"]!.AsArray()[0]!
                .GetValue<string>()
                .Should()
                .Be("Missing required 'claimSets' property");
        }

        [Test]
        public async Task It_returns_a_compliant_bad_request_400_for_malformed_input_failures()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [new ClaimsFailure("JsonError", "Invalid JSON format in uploaded claims document")]
                    )
                );

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            await AssertBadRequestContract(response, "The claims upload request was invalid.");
        }

        [Test]
        public async Task It_returns_a_compliant_500_for_database_failures_without_leaking_details()
        {
            const string sensitive =
                "Npgsql connection host=db.internal;Password=sup3rsecret failed executing SELECT * FROM dmscs.claimset";
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(new ClaimsLoadStatus(false, [new ClaimsFailure("Database", sensitive)]));

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            string rawBody = await response.Content.ReadAsStringAsync();
            rawBody.Should().NotContain("sup3rsecret");
            rawBody.Should().NotContain("SELECT");
            rawBody.Should().NotContain("db.internal");
            await AssertInternalServerErrorContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_500_for_unexpected_failures()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [
                            new ClaimsFailure(
                                "Unexpected",
                                "Object reference not set to an instance of an object."
                            ),
                        ]
                    )
                );

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            await AssertInternalServerErrorContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_500_for_operation_error_failures()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [
                            new ClaimsFailure(
                                "OperationError",
                                "Invalid operation occurred during claims upload"
                            ),
                        ]
                    )
                );

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            await AssertInternalServerErrorContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_400_when_the_service_throws_json_exception()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Throws(new JsonException("secret parse detail"));

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            await AssertBadRequestContract(
                response,
                "The request body could not be parsed as valid claims JSON."
            );
        }

        [Test]
        public async Task It_returns_a_compliant_500_when_the_service_throws_invalid_operation()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Throws(new InvalidOperationException("secret operation detail"));

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            await AssertInternalServerErrorContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_400_when_the_service_throws_argument_exception()
        {
            const string sensitive = "secret argument detail host=db.internal;Password=sup3rsecret";
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Throws(new ArgumentException(sensitive));

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            string rawBody = await response.Content.ReadAsStringAsync();
            rawBody.Should().NotContain("sup3rsecret");
            rawBody.Should().NotContain("secret argument detail");
            await AssertBadRequestContract(response, "The claims upload request was invalid.");
        }

        [Test]
        public async Task It_returns_a_compliant_bad_request_400_for_argument_error_failures()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [new ClaimsFailure("ArgumentError", "Invalid claims data provided for upload")]
                    )
                );

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            await AssertBadRequestContract(response, "The claims upload request was invalid.");
        }

        [Test]
        public async Task It_returns_a_compliant_500_for_unknown_failures()
        {
            A.CallTo(() => ClaimsUploadService.UploadClaimsAsync(A<JsonNode>._))
                .Returns(
                    new ClaimsLoadStatus(
                        false,
                        [new ClaimsFailure("Unknown", "Unexpected result type: SomethingUnexpected")]
                    )
                );

            var response = await Client.PostAsync(UploadClaimsRoute, ClaimsBody());

            await AssertInternalServerErrorContract(response);
        }
    }

    /// <summary>
    /// A request with no bearer token is challenged by the framework; the resulting 401 carries the
    /// Ed-Fi authentication contract instead of an empty framework body.
    /// </summary>
    [TestFixture]
    public class Given_no_bearer_token_the_framework_401_is_compliant : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() => ArrangeUnauthenticatedClient(dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_a_compliant_401_for_reload_claims()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            await AssertUnauthorizedContract(response, MissingCredentialError);
        }

        [Test]
        public async Task It_returns_a_compliant_401_for_current_claims()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            await AssertUnauthorizedContract(response, MissingCredentialError);
        }
    }

    /// <summary>
    /// A malformed (non-JWT) bearer token is rejected by the production JWT handler; the resulting
    /// 401 carries the Ed-Fi authentication contract and reports the credential as invalid rather than
    /// missing.
    /// </summary>
    [TestFixture]
    public class Given_an_invalid_bearer_token_the_framework_401_is_compliant : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() => ArrangeClientWithInvalidBearerToken(dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_a_compliant_401_for_current_claims()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            await AssertUnauthorizedContract(response, InvalidCredentialError);
        }
    }

    /// <summary>
    /// An authenticated principal that lacks the configuration-service role is forbidden by the
    /// framework; the resulting 403 carries the Ed-Fi authorization contract.
    /// </summary>
    [TestFixture]
    public class Given_a_token_without_the_configuration_service_role_the_framework_403_is_compliant
        : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AdminScope.Name,
                dangerousFlagEnabled: true,
                requiredServiceRole: RoleTheTokenDoesNotHave
            );

        [Test]
        public async Task It_returns_a_compliant_403_for_reload_claims()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            await AssertForbiddenContract(response);
        }

        [Test]
        public async Task It_returns_a_compliant_403_for_current_claims()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            await AssertForbiddenContract(response);
        }
    }
}
