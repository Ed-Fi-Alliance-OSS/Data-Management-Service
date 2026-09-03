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
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
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
using Microsoft.Extensions.Logging;
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
    /// Configures the shared IClaimsProvider fake with the claims document GetCurrentClaims returns and
    /// the reload id both claims endpoints report.
    /// </summary>
    protected void ArrangeCurrentClaims(JsonNode claimSetsNode, JsonNode claimsHierarchyNode, Guid reloadId)
    {
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes())
            .Returns(new ClaimsDocument(claimSetsNode, claimsHierarchyNode));
        A.CallTo(() => _claimsProvider.ReloadId).Returns(reloadId);
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

    // A non-empty JSON object in the canonical (unwrapped) upload shape. Fixtures using it stub the
    // upload service, so only "reaches the handler as a JSON object" matters to them.
    protected static StringContent NonEmptyUploadBody() =>
        new("{\"claimSets\":[],\"claimsHierarchy\":[]}", Encoding.UTF8, "application/json");

    /// <summary>
    /// Builds a real <see cref="ClaimsUploadService"/> whose persistence layer reports success, so a
    /// fixture exercises the production upload path (structure checks, validation, failure mapping)
    /// instead of a stubbed status. Pass the real <see cref="ClaimsValidator"/> to additionally prove the
    /// payload satisfies the canonical claims JSON schema.
    /// </summary>
    protected static ClaimsUploadService CreateRealUploadService(IClaimsValidator claimsValidator)
    {
        IClaimsDataLoader claimsDataLoader = A.Fake<IClaimsDataLoader>();
        A.CallTo(() => claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
            .Returns(new ClaimsDataLoadResult.Success(ClaimSetsLoaded: 1, HierarchyLoaded: true));

        return new ClaimsUploadService(
            A.Fake<ILogger<ClaimsUploadService>>(),
            A.Fake<IClaimsProvider>(),
            claimsDataLoader,
            claimsValidator
        );
    }

    /// <summary>
    /// Asserts the 400 produced by a body that carries neither canonical claims-document property: one
    /// validation entry per property, keyed by the property name the caller must supply.
    /// </summary>
    protected static async Task AssertMissingClaimsDocumentPropertiesContract(HttpResponseMessage response) =>
        await AssertDataValidationContract(
            response,
            validationErrors =>
            {
                validationErrors.Count.Should().Be(2);
                validationErrors["claimSets"]!
                    .AsArray()
                    .Select(node => node!.GetValue<string>())
                    .Should()
                    .Equal("Missing required 'claimSets' property");
                validationErrors["claimsHierarchy"]!
                    .AsArray()
                    .Select(node => node!.GetValue<string>())
                    .Should()
                    .Equal("Missing required 'claimsHierarchy' property");
            }
        );

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
        string? requiredServiceRole = null,
        IClaimsUploadService? uploadServiceOverride = null
    )
    {
        _factory = CreateFactory(
            addTestAuthentication: true,
            dangerousFlagEnabled,
            requiredServiceRole,
            uploadServiceOverride
        );
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
    }

    private WebApplicationFactory<Program> CreateFactory(
        bool addTestAuthentication,
        bool dangerousFlagEnabled,
        string? requiredServiceRole,
        IClaimsUploadService? uploadServiceOverride = null
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

                        collection.AddTransient(_ => uploadServiceOverride ?? _claimsUploadService);
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

    /// <summary>A reload failure result becomes the internal-server-error contract (500).</summary>
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

    /// <summary>A JsonException during reload becomes a generic bad-request (400).</summary>
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

    /// <summary>An InvalidOperationException during reload becomes 500.</summary>
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

    /// <summary>
    /// An empty JSON object is a well-formed request whose two canonical claims properties are both
    /// absent, so the response names those properties rather than a request wrapper.
    /// </summary>
    [TestFixture]
    public class Given_upload_claims_receives_an_empty_json_object : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AdminScope.Name,
                dangerousFlagEnabled: true,
                uploadServiceOverride: CreateRealUploadService(A.Fake<IClaimsValidator>())
            );

        [Test]
        public async Task It_should_report_both_missing_claims_document_properties()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, EmptyJsonBody());
            await AssertMissingClaimsDocumentPropertiesContract(response);
        }
    }

    /// <summary>
    /// The endpoint accepts only the canonical claims document, so a body nesting that document under a
    /// "claims" property is rejected with the same actionable 400 that names the two required
    /// properties. The canonical schema forbids the nested shape (root additionalProperties: false).
    /// </summary>
    [TestFixture]
    public class Given_upload_claims_receives_a_nested_claims_property : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AdminScope.Name,
                dangerousFlagEnabled: true,
                uploadServiceOverride: CreateRealUploadService(A.Fake<IClaimsValidator>())
            );

        [Test]
        public async Task It_should_report_both_missing_claims_document_properties()
        {
            using var nestedBody = new StringContent(
                """{"claims":{"claimSets":[],"claimsHierarchy":[]}}""",
                Encoding.UTF8,
                "application/json"
            );

            var response = await Client.PostAsync(UploadClaimsRoute, nestedBody);
            await AssertMissingClaimsDocumentPropertiesContract(response);
        }
    }

    /// <summary>Path-bearing validation failures are grouped into validationErrors by path.</summary>
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
                        // Surrounding whitespace proves the path is trimmed before becoming the key.
                        new ClaimsFailure("Validation", "unknown claim", "  $.claimsHierarchy[0]  "),
                    ]
                )
            );
        }

        [Test]
        public async Task It_should_group_validation_errors_by_trimmed_path_with_exact_messages()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertDataValidationContract(
                response,
                validationErrors =>
                {
                    validationErrors.Count.Should().Be(2);

                    validationErrors["$.claimSets[0].name"]!
                        .AsArray()
                        .Select(n => n!.GetValue<string>())
                        .Should()
                        .Equal("must not be empty", "must be unique");

                    // Keyed by the trimmed path, not the whitespace-padded original.
                    validationErrors.ContainsKey("  $.claimsHierarchy[0]  ").Should().BeFalse();
                    validationErrors["$.claimsHierarchy[0]"]!
                        .AsArray()
                        .Select(n => n!.GetValue<string>())
                        .Should()
                        .Equal("unknown claim");
                }
            );
        }
    }

    /// <summary>The two fixed structure literals map to claimSets/claimsHierarchy keys.</summary>
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
        public async Task It_should_return_the_internal_server_error_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertInternalServerErrorContract(response, Sentinel);
        }
    }

    /// <summary>A pathless "Validation" failure is denied; its message must not leak.</summary>
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

    /// <summary>An unrecognized/future failure type is denied even with a path.</summary>
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
        public async Task It_should_return_a_fully_generic_500_without_the_safe_entry()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertInternalServerErrorContract(response, UnsafeSentinel);

            string content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain(SafePath);
            content.Should().NotContain(SafeMessage);
        }
    }

    [TestFixture]
    public class Given_a_real_upload_service_whose_validator_throws : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_VALIDATOR_THROW_4d5e_must_not_leak";

        [SetUp]
        public void Setup()
        {
            var throwingValidator = A.Fake<IClaimsValidator>();
            A.CallTo(() => throwingValidator.Validate(A<JsonNode>._))
                .Throws(new InvalidOperationException(Sentinel));

            var realUploadService = new ClaimsUploadService(
                A.Fake<ILogger<ClaimsUploadService>>(),
                A.Fake<IClaimsProvider>(),
                A.Fake<IClaimsDataLoader>(),
                throwingValidator
            );
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AdminScope.Name,
                dangerousFlagEnabled: true,
                uploadServiceOverride: realUploadService
            );
        }

        [Test]
        public async Task It_should_return_the_internal_server_error_contract()
        {
            using var content = new StringContent(
                """{"claimSets":[],"claimsHierarchy":[]}""",
                Encoding.UTF8,
                "application/json"
            );
            var response = await Client.PostAsync(UploadClaimsRoute, content);
            await AssertInternalServerErrorContract(response, Sentinel);
        }
    }

    /// <summary>A JsonException during upload becomes a generic bad-request (400).</summary>
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

    /// <summary>An ArgumentException during upload becomes a generic bad-request (400).</summary>
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

    /// <summary>An InvalidOperationException during upload becomes 500.</summary>
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

    /// <summary>
    /// A malformed JSON request body fails Minimal API model binding before the handler runs. Before
    /// RouteHandlerOptions.ThrowOnBadRequest was enabled (Program.cs), this was an empty 400 body that
    /// never reached GlobalExceptionHandler; it now returns the full data-validation contract via the
    /// real production pipeline, in the non-Development-equivalent "Test" environment.
    /// </summary>
    [TestFixture]
    public class Given_upload_claims_receives_a_malformed_json_body : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_the_data_validation_contract_instead_of_an_empty_body()
        {
            using var malformedBody = new StringContent(
                "{\"claimSets\":[],}",
                Encoding.UTF8,
                "application/json"
            );

            var response = await Client.PostAsync(UploadClaimsRoute, malformedBody);

            await AssertDataValidationContract(
                response,
                validationErrors =>
                {
                    validationErrors.Count.Should().Be(1);
                    validationErrors["$"]!
                        .AsArray()
                        .Select(node => node!.GetValue<string>())
                        .Should()
                        .Equal("The request body contains invalid JSON.");
                }
            );

            string content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain("JsonException");
            content.Should().NotContain("System.Text.Json");
            content.Should().NotContain("LineNumber");
        }
    }

    [TestFixture]
    public class Given_upload_claims_receives_a_non_object_claims_payload : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup()
        {
            var realUploadService = new ClaimsUploadService(
                A.Fake<ILogger<ClaimsUploadService>>(),
                A.Fake<IClaimsProvider>(),
                A.Fake<IClaimsDataLoader>(),
                A.Fake<IClaimsValidator>()
            );
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AdminScope.Name,
                dangerousFlagEnabled: true,
                uploadServiceOverride: realUploadService
            );
        }

        [TestCase("[]")]
        [TestCase("\"x\"")]
        public async Task It_returns_the_data_validation_contract_at_the_document_root(string requestBody)
        {
            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var response = await Client.PostAsync(UploadClaimsRoute, content);

            await AssertDataValidationContract(
                response,
                validationErrors =>
                {
                    validationErrors.Count.Should().Be(1);
                    validationErrors["$"]!
                        .AsArray()
                        .Select(node => node!.GetValue<string>())
                        .Should()
                        .Equal("Claims JSON must be an object");
                }
            );
        }
    }

    [TestFixture]
    public class Given_upload_returns_no_failures : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(new ClaimsLoadStatus(false, []));
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response);
        }
    }

    [TestFixture]
    public class Given_upload_returns_an_unrecognized_structure_message : ClaimsManagementModuleTests
    {
        private const string Sentinel = "SENTINEL_UPLOAD_STRUCTURE_7d2e_must_not_leak";

        [SetUp]
        public void Setup()
        {
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);
            ArrangeUploadReturns(
                new ClaimsLoadStatus(false, [new ClaimsFailure("Structure", Sentinel, "$.some.path")])
            );
        }

        [Test]
        public async Task It_should_return_the_generic_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, NonEmptyUploadBody());
            await AssertGenericBadRequestContract(response, Sentinel);
        }
    }

    /// <summary>
    /// The upload request body stays required: an absent body is rejected by model binding before the
    /// handler runs, keeping the same missing-body contract every other CMS write endpoint returns.
    /// </summary>
    [TestFixture]
    public class Given_upload_claims_receives_no_request_body : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_the_missing_body_bad_request_contract()
        {
            var response = await Client.PostAsync(UploadClaimsRoute, content: null);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

            JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
            body["title"]!.GetValue<string>().Should().Be("Bad Request");
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("A non-empty request body is required.");
        }
    }

    /// <summary>
    /// A literal JSON null body deserializes to no document at all, so it is treated as a missing body
    /// rather than reaching the upload service.
    /// </summary>
    [TestFixture]
    public class Given_upload_claims_receives_a_literal_null_body : ClaimsManagementModuleTests
    {
        [SetUp]
        public void Setup() =>
            ArrangeAuthenticatedClient(AuthorizationScopes.AdminScope.Name, dangerousFlagEnabled: true);

        [Test]
        public async Task It_returns_the_missing_body_bad_request_contract()
        {
            using var nullBody = new StringContent("null", Encoding.UTF8, "application/json");

            var response = await Client.PostAsync(UploadClaimsRoute, nullBody);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

            JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
            body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("A non-empty request body is required.");
        }
    }

    /// <summary>
    /// The uniform-contract guarantee: the exact body returned by GET /management/current-claims is
    /// accepted by POST /management/upload-claims with no wrapping, editing or reformatting. The real
    /// ClaimsValidator is used, so this also proves the downloaded document satisfies the canonical
    /// claims JSON schema.
    /// </summary>
    [TestFixture]
    public class Given_the_current_claims_response_is_uploaded_unchanged : ClaimsManagementModuleTests
    {
        private static readonly Guid _currentReloadId = new("11111111-2222-3333-4444-555555555555");

        private HttpResponseMessage _getResponse = null!;
        private string _downloadedClaims = null!;
        private HttpResponseMessage _uploadResponse = null!;
        private JsonObject _uploadResponseBody = null!;

        [SetUp]
        public async Task Setup()
        {
            ArrangeCurrentClaims(
                JsonNode.Parse("""[{ "claimSetName": "RoundTripClaimSet", "isSystemReserved": false }]""")!,
                JsonNode.Parse(
                    """
                    [
                      {
                        "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
                        "claimSets": [{ "name": "RoundTripClaimSet", "actions": [{ "name": "Read" }] }]
                      }
                    ]
                    """
                )!,
                _currentReloadId
            );
            ArrangeAuthenticatedClient(
                AuthorizationScopes.AdminScope.Name,
                dangerousFlagEnabled: true,
                uploadServiceOverride: CreateRealUploadService(
                    new ClaimsValidator(A.Fake<ILogger<ClaimsValidator>>())
                )
            );

            _getResponse = await Client.GetAsync(CurrentClaimsRoute);
            _downloadedClaims = await _getResponse.Content.ReadAsStringAsync();

            // Posted back exactly as downloaded: no wrapper, no re-serialization.
            using StringContent uploadBody = new(_downloadedClaims, Encoding.UTF8, "application/json");
            _uploadResponse = await Client.PostAsync(UploadClaimsRoute, uploadBody);
            _uploadResponseBody = JsonNode
                .Parse(await _uploadResponse.Content.ReadAsStringAsync())!
                .AsObject();
        }

        [TearDown]
        public void DisposeResponses()
        {
            _getResponse?.Dispose();
            _uploadResponse?.Dispose();
        }

        [Test]
        public void It_returns_the_current_claims_with_the_reload_id_header()
        {
            _getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            _getResponse.Headers.GetValues("X-Reload-Id").Should().Equal(_currentReloadId.ToString());
        }

        [Test]
        public void It_downloads_the_canonical_claims_document_shape() =>
            JsonNode
                .Parse(_downloadedClaims)!
                .AsObject()
                .Select(property => property.Key)
                .Should()
                .Equal("claimSets", "claimsHierarchy");

        [Test]
        public void It_accepts_the_downloaded_document_without_modification() =>
            _uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_reports_a_successful_upload_with_a_reload_id()
        {
            _uploadResponseBody["success"]!.GetValue<bool>().Should().BeTrue();
            Guid.TryParse(_uploadResponseBody["reloadId"]!.GetValue<string>(), out _).Should().BeTrue();
        }

        [Test]
        public void It_reports_no_validation_errors() =>
            _uploadResponseBody.ContainsKey("validationErrors").Should().BeFalse();
    }
}
