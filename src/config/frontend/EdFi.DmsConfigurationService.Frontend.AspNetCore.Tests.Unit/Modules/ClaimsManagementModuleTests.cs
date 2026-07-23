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
    /// Asserts the full Ed-Fi not-found error contract for a disabled claims-management endpoint,
    /// including the exact route-specific <paramref name="expectedDetail"/>.
    /// </summary>
    protected static async Task AssertNotFoundContract(HttpResponseMessage response, string expectedDetail)
    {
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        JsonNode body = JsonNode.Parse(content)!;
        body["detail"]!.GetValue<string>().Should().Be(expectedDetail);
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
        body["title"]!.GetValue<string>().Should().Be("Not Found");
        body["status"]!.GetValue<int>().Should().Be(404);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }

    /// <summary>
    /// Configures the shared IClaimsProvider fake so that GetCurrentClaims' document build throws.
    /// </summary>
    protected void ArrangeCurrentClaimsToThrow(Exception exception)
    {
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes()).Throws(exception);
    }

    /// <summary>
    /// Asserts the full Ed-Fi internal-server-error contract for a 500 response and proves the raw
    /// exception text (<paramref name="secretThatMustNotLeak"/>) is absent from the body, including
    /// the legacy ad-hoc singular <c>error</c>/<c>message</c> members.
    /// </summary>
    protected static async Task AssertInternalServerErrorContract(
        HttpResponseMessage response,
        string secretThatMustNotLeak
    )
    {
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        content.Should().NotContain(secretThatMustNotLeak);

        JsonNode body = JsonNode.Parse(content)!;
        body["detail"]!.GetValue<string>().Should().BeEmpty();
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");
        body["title"]!.GetValue<string>().Should().Be("Internal Server Error");
        body["status"]!.GetValue<int>().Should().Be(500);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);

        JsonObject bodyObject = body.AsObject();
        bodyObject.ContainsKey("error").Should().BeFalse();
        bodyObject.ContainsKey("message").Should().BeFalse();
    }

    protected void ArrangeReloadReturns(ClaimsLoadStatus status) =>
        A.CallTo(() => _claimsUploadService.ReloadClaimsAsync()).Returns(status);

    protected void ArrangeReloadThrows(Exception exception) =>
        A.CallTo(() => _claimsUploadService.ReloadClaimsAsync()).Throws(exception);

    protected void ArrangeUploadReturns(ClaimsLoadStatus status) =>
        A.CallTo(() => _claimsUploadService.UploadClaimsAsync(A<JsonNode>._)).Returns(status);

    protected void ArrangeUploadThrows(Exception exception) =>
        A.CallTo(() => _claimsUploadService.UploadClaimsAsync(A<JsonNode>._)).Throws(exception);

    protected static StringContent NonEmptyUploadBody() =>
        new("{\"claims\":{\"placeholder\":true}}", Encoding.UTF8, "application/json");

    /// <summary>
    /// Asserts the generic Ed-Fi bad-request contract (empty extension members, fixed safe detail) and
    /// proves neither the optional secret nor the legacy failure-DTO shape appears in the body.
    /// </summary>
    protected static async Task AssertGenericBadRequestContract(
        HttpResponseMessage response,
        string? secretThatMustNotLeak = null
    )
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        if (secretThatMustNotLeak is not null)
        {
            content.Should().NotContain(secretThatMustNotLeak);
        }

        JsonNode body = JsonNode.Parse(content)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        body["title"]!.GetValue<string>().Should().Be("Bad Request");
        body["detail"]!.GetValue<string>().Should().Be("The request could not be processed.");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
        AssertNoLegacyFailureShape(body.AsObject());
    }

    /// <summary>
    /// Asserts the Ed-Fi data-validation contract and delegates the <c>validationErrors</c> shape to
    /// <paramref name="assertValidationErrors"/>.
    /// </summary>
    protected static async Task AssertDataValidationContract(
        HttpResponseMessage response,
        Action<JsonObject> assertValidationErrors
    )
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        JsonNode body = JsonNode.Parse(content)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data");
        body["title"]!.GetValue<string>().Should().Be("Data Validation Failed");
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["errors"]!.AsArray().Count.Should().Be(0);
        assertValidationErrors(body["validationErrors"]!.AsObject());
        AssertNoLegacyFailureShape(body.AsObject());
    }

    // Proves the old ReloadClaimsResponse / UploadClaimsResponse.Failed DTO shape (success/errors/…) is gone.
    private static void AssertNoLegacyFailureShape(JsonObject body)
    {
        body.ContainsKey("success").Should().BeFalse();
        body.ContainsKey("Success").Should().BeFalse();
        body.ContainsKey("error").Should().BeFalse();
        body.ContainsKey("message").Should().BeFalse();
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
            await AssertNotFoundContract(response, "Claims reload endpoint is not available.");
        }

        [Test]
        public async Task It_should_return_404_for_upload_claims()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            await AssertNotFoundContract(response, "Claims upload endpoint is not available.");
        }

        [Test]
        public async Task It_should_return_404_for_current_claims()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            await AssertNotFoundContract(response, "Current claims endpoint is not available.");
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
    /// With the dangerous flag enabled and an authorized caller, a JsonException thrown while building
    /// the current-claims document is caught and returned as the Ed-Fi internal-server-error contract,
    /// never leaking the exception message.
    /// </summary>
    [TestFixture]
    public class Given_current_claims_throws_a_json_exception : ClaimsManagementModuleTests
    {
        private const string SecretSentinel = "SENTINEL_JSON_a91f3c2e_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeCurrentClaimsToThrow(new JsonException(SecretSentinel));
        }

        [Test]
        public async Task It_should_return_the_internal_server_error_contract()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            await AssertInternalServerErrorContract(response, SecretSentinel);
        }
    }

    /// <summary>
    /// With the dangerous flag enabled and an authorized caller, an InvalidOperationException thrown
    /// while building the current-claims document is caught and returned as the Ed-Fi
    /// internal-server-error contract, never leaking the exception message.
    /// </summary>
    [TestFixture]
    public class Given_current_claims_throws_an_invalid_operation_exception : ClaimsManagementModuleTests
    {
        private const string SecretSentinel = "SENTINEL_INVOP_5d7b1c04_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeCurrentClaimsToThrow(new InvalidOperationException(SecretSentinel));
        }

        [Test]
        public async Task It_should_return_the_internal_server_error_contract()
        {
            var response = await Client.GetAsync(CurrentClaimsRoute);
            await AssertInternalServerErrorContract(response, SecretSentinel);
        }
    }

    // ---- C06: reload/upload non-success DTO conversion (INV-08..15) ----

    /// <summary>INV-08: a reload failure result becomes the internal-server-error contract (500).</summary>
    [TestFixture]
    public class Given_reload_returns_a_failure_status : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_RELOAD_FAIL_1a2b_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeReloadReturns(new ClaimsLoadStatus(false, [new ClaimsFailure("Database", Sentinel)]));
        }

        [Test]
        public async Task It_should_return_the_internal_server_error_contract()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            await AssertInternalServerErrorContract(response, Sentinel);
        }
    }

    /// <summary>INV-09: a JsonException during reload becomes a generic bad-request (400).</summary>
    [TestFixture]
    public class Given_reload_throws_a_json_exception : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_RELOAD_JSON_3c4d_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeReloadThrows(new JsonException(Sentinel));
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            await AssertGenericBadRequestContract(response, Sentinel);
        }
    }

    /// <summary>INV-10: an InvalidOperationException during reload becomes 500.</summary>
    [TestFixture]
    public class Given_reload_throws_an_invalid_operation_exception : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_RELOAD_INVOP_5e6f_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeReloadThrows(new InvalidOperationException(Sentinel));
        }

        [Test]
        public async Task It_should_return_the_internal_server_error_contract()
        {
            var response = await Client.PostAsync(ReloadClaimsRoute, EmptyJsonBody());
            await AssertInternalServerErrorContract(response, Sentinel);
        }
    }

    /// <summary>INV-11: a null Claims body becomes data validation keyed by "Claims" (400).</summary>
    [TestFixture]
    public class Given_upload_claims_is_null : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);

        [Test]
        public async Task It_should_return_data_validation_for_the_claims_field()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            await AssertDataValidationContract(
                response,
                validationErrors =>
                {
                    validationErrors.Count.Should().Be(1);
                    validationErrors["Claims"]!.AsArray().Count.Should().Be(1);
                    validationErrors["Claims"]![0]!.GetValue<string>().Should().Be("Claims JSON is required");
                }
            );
        }
    }

    /// <summary>INV-12 (safe): path-bearing validation failures are grouped into validationErrors by path.</summary>
    [TestFixture]
    public class Given_upload_returns_path_bearing_validation_failures : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(
                new ClaimsLoadStatus(
                    false,
                    [
                        new ClaimsFailure("Validation", "must not be empty", "$.claimSets[0].name"),
                        new ClaimsFailure("Validation", "must be unique", "$.claimSets[0].name"),
                        new ClaimsFailure("Validation", "unknown claim", "$.claimsHierarchy[0]"),
                    ]
                )
            );
        }

        [Test]
        public async Task It_should_group_validation_errors_by_path()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertDataValidationContract(
                response,
                validationErrors =>
                {
                    validationErrors.Count.Should().Be(2);
                    validationErrors["$.claimSets[0].name"]!.AsArray().Count.Should().Be(2);
                    validationErrors["$.claimsHierarchy[0]"]!.AsArray().Count.Should().Be(1);
                }
            );
        }
    }

    /// <summary>INV-12 (safe): the two fixed structure literals map to claimSets/claimsHierarchy keys.</summary>
    [TestFixture]
    public class Given_upload_returns_the_structure_literals : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(
                new ClaimsLoadStatus(
                    false,
                    [
                        new ClaimsFailure("Structure", "Missing required 'claimSets' property"),
                        new ClaimsFailure("Structure", "Missing required 'claimsHierarchy' property"),
                    ]
                )
            );
        }

        [Test]
        public async Task It_should_map_structure_literals_to_field_keys()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertDataValidationContract(
                response,
                validationErrors =>
                {
                    validationErrors.Count.Should().Be(2);
                    validationErrors.ContainsKey("claimSets").Should().BeTrue();
                    validationErrors.ContainsKey("claimsHierarchy").Should().BeTrue();
                }
            );
        }
    }

    /// <summary>INV-12 (unsafe): a database failure is denied and returned as a generic bad-request.</summary>
    [TestFixture]
    public class Given_upload_returns_a_database_failure : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_UPLOAD_DB_7a8b_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(new ClaimsLoadStatus(false, [new ClaimsFailure("Database", Sentinel)]));
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response, Sentinel);
        }
    }

    /// <summary>INV-12 (unsafe): a pathless "Validation" failure is denied; its message must not leak.</summary>
    [TestFixture]
    public class Given_upload_returns_a_pathless_validation_failure : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_UPLOAD_PATHLESS_9c0d_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(new ClaimsLoadStatus(false, [new ClaimsFailure("Validation", Sentinel)]));
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response, Sentinel);
        }
    }

    /// <summary>INV-12 (unsafe): an unrecognized/future failure type is denied even with a path.</summary>
    [TestFixture]
    public class Given_upload_returns_an_unrecognized_failure_type : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_UPLOAD_FUTURE_1e2f_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(
                new ClaimsLoadStatus(false, [new ClaimsFailure("FutureType", Sentinel, "$.some.path")])
            );
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response, Sentinel);
        }
    }

    /// <summary>INV-12 (mixed): any unsafe failure forces a fully generic body; safe entries are dropped.</summary>
    [TestFixture]
    public class Given_upload_returns_a_mixed_safe_and_unsafe_set : ClaimsManagementModuleTests
    {
        private const string UnsafeSentinel = "SENTINEL_MIXED_DB_3a4b_must_not_leak";
        private const string SafePath = "SENTINEL_SAFE_PATH_must_not_appear";
        private const string SafeMessage = "SENTINEL_SAFE_MESSAGE_must_not_appear";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(
                new ClaimsLoadStatus(
                    false,
                    [
                        new ClaimsFailure("Validation", SafeMessage, SafePath),
                        new ClaimsFailure("Database", UnsafeSentinel),
                    ]
                )
            );
        }

        [Test]
        public async Task It_should_return_a_fully_generic_body_without_the_safe_entry()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response, UnsafeSentinel);

            string content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain(SafePath);
            content.Should().NotContain(SafeMessage);
        }
    }

    /// <summary>INV-13: a JsonException during upload becomes a generic bad-request (400).</summary>
    [TestFixture]
    public class Given_upload_throws_a_json_exception : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_UPLOAD_JSON_5c6d_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadThrows(new JsonException(Sentinel));
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response, Sentinel);
        }
    }

    /// <summary>INV-14: an ArgumentException during upload becomes a generic bad-request (400).</summary>
    [TestFixture]
    public class Given_upload_throws_an_argument_exception : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_UPLOAD_ARG_7e8f_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadThrows(new ArgumentException(Sentinel));
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response, Sentinel);
        }
    }

    /// <summary>INV-15: an InvalidOperationException during upload becomes 500.</summary>
    [TestFixture]
    public class Given_upload_throws_an_invalid_operation_exception : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_UPLOAD_INVOP_9a0b_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadThrows(new InvalidOperationException(Sentinel));
        }

        [Test]
        public async Task It_should_return_the_internal_server_error_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertInternalServerErrorContract(response, Sentinel);
        }
    }
}
