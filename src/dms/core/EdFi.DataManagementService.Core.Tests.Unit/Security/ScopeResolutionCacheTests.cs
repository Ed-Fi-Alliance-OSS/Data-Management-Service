// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Management;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

/// <summary>
/// The JWT validation cache holds unresolved authorization facts. Claim-set resolution runs per
/// request against the requesting tenant's current metadata, so a cached token can resolve
/// differently across tenants and after a metadata change, and resolution never writes back into
/// the cached record. Role-only endpoints keep authorizing on the principal alone.
/// </summary>
public class ScopeResolutionCacheTests
{
    private const string Audience = "ed-fi-ods-api";
    private const string RoleClaimType = "role";
    private const string RequiredRole = "dms-operator";
    private const string TenantA = "TenantA";
    private const string TenantB = "TenantB";

    [TestFixture]
    [Parallelizable]
    public class Given_A_Cached_Jwt_Used_For_ClaimSet_Resolution
    {
        private static readonly string _identityClaimUri =
            $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity";

        private static ClaimSet IdentityClaimSet(string name) =>
            new(
                name,
                [
                    new ResourceClaim(
                        _identityClaimUri,
                        "Create",
                        [
                            new AuthorizationStrategy(
                                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                            ),
                        ]
                    ),
                ]
            );

        private JwtValidationService _validator = null!;
        private IConfigurationManager<OpenIdConnectConfiguration> _configurationManager = null!;
        private IClaimSetProvider _claimSetProvider = null!;
        private ClientAuthorizations _cachedAuthorizations = null!;
        private readonly List<ClientAuthorizations> _authorizationsSeenByConsumer = [];
        private RequestInfo _tenantAFirstRequest = null!;
        private RequestInfo _tenantBRequest = null!;
        private RequestInfo _tenantAAfterMetadataChangeRequest = null!;
        private int _tenantAFirstContinuations;
        private int _tenantBContinuations;
        private int _tenantAAfterMetadataChangeContinuations;
        private ClientAuthorizations _revalidatedAuthorizations = null!;
        private readonly Dictionary<string, EndpointRoleAuthorizationResult> _managementResults = [];
        private readonly Dictionary<string, DocumentCacheStatusAuthorizationResult> _documentCacheResults =
        [];

        [SetUp]
        public async Task Setup()
        {
            // NUnit reuses the fixture instance across tests, and Setup reruns before each one.
            _authorizationsSeenByConsumer.Clear();
            _managementResults.Clear();
            _documentCacheResults.Clear();

            (
                _validator,
                _configurationManager,
                IOptions<JwtAuthenticationOptions> jwtOptions,
                _,
                JwtSecurityTokenHandler tokenHandler,
                RsaSecurityKey signingKey
            ) = JwtValidationServiceTests.CreateService(options => options.RoleClaimType = RoleClaimType);

            string authority = jwtOptions.Value.Authority;
            OpenIdConnectConfiguration metadata = new() { Issuer = authority };
            metadata.SigningKeys.Add(signingKey);
            A.CallTo(() => _configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(metadata));

            string token = JwtValidationServiceTests.CreateTestToken(
                [
                    new Claim("jti", "cached-resolution-token"),
                    new Claim("client_id", "cached-resolution-client"),
                    new Claim("scope", "profile Alpha"),
                    new Claim("scope", "Beta"),
                ],
                authority,
                Audience,
                signingKey,
                tokenHandler
            );

            // Mutable per-tenant metadata standing in for each tenant's claim-set snapshot.
            Dictionary<string, ClaimSet[]> metadataByTenant = new()
            {
                [TenantA] = [IdentityClaimSet("Alpha")],
                [TenantB] = [IdentityClaimSet("Beta")],
            };
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .ReturnsLazily(
                    (string? tenant, CancellationToken _) =>
                        Task.FromResult<IList<ClaimSet>>([.. metadataByTenant[tenant!]])
                );

            // Populate the validation cache before any request resolves a claim set.
            _cachedAuthorizations = (
                await _validator.ValidateAndExtractClientAuthorizationsAsync(token, CancellationToken.None)
            ).ClientAuthorizations!;

            (_tenantAFirstRequest, _tenantAFirstContinuations) = await SendIdentityRequest(token, TenantA);
            (_tenantBRequest, _tenantBContinuations) = await SendIdentityRequest(token, TenantB);

            // One match becomes two while the JWT stays cached.
            metadataByTenant[TenantA] = [IdentityClaimSet("Alpha"), IdentityClaimSet("Beta")];

            (_tenantAAfterMetadataChangeRequest, _tenantAAfterMetadataChangeContinuations) =
                await SendIdentityRequest(token, TenantA);

            _revalidatedAuthorizations = (
                await _validator.ValidateAndExtractClientAuthorizationsAsync(token, CancellationToken.None)
            ).ClientAuthorizations!;

            await AuthorizeRoleOnlyEndpoints(authority, tokenHandler, signingKey);
        }

        private async Task<(RequestInfo RequestInfo, int Continuations)> SendIdentityRequest(
            string token,
            string tenant
        )
        {
            RequestInfo requestInfo = new(
                new FrontendRequest(
                    Path: "/identity/v2/identities",
                    Body: null,
                    Form: null,
                    Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
                    QueryParameters: [],
                    TraceId: new TraceId($"trace-{tenant}"),
                    RouteQualifiers: [],
                    Tenant: tenant
                ),
                RequestMethod.POST,
                No.ServiceProvider
            )
            {
                IdentityOperation = IdentityOperation.Create,
            };

            JwtAuthenticationMiddleware authentication = new(
                _validator,
                NullLogger<JwtAuthenticationMiddleware>.Instance
            );
            ServiceClaimAuthorizationMiddleware authorization = new(
                _claimSetProvider,
                NullLogger<ServiceClaimAuthorizationMiddleware>.Instance
            );
            int continuations = 0;

            await authentication.Execute(
                requestInfo,
                async () =>
                {
                    _authorizationsSeenByConsumer.Add(requestInfo.ClientAuthorizations);
                    await authorization.Execute(
                        requestInfo,
                        () =>
                        {
                            continuations++;
                            return Task.CompletedTask;
                        }
                    );
                }
            );

            return (requestInfo, continuations);
        }

        private async Task AuthorizeRoleOnlyEndpoints(
            string authority,
            JwtSecurityTokenHandler tokenHandler,
            RsaSecurityKey signingKey
        )
        {
            Dictionary<string, Claim[]> tokens = new()
            {
                ["no-scope"] = [new Claim("jti", "role-no-scope"), new Claim(RoleClaimType, RequiredRole)],
                ["multi-scope"] =
                [
                    new Claim("jti", "role-multi-scope"),
                    new Claim("scope", "Alpha Beta"),
                    new Claim("scope", "profile"),
                    new Claim(RoleClaimType, RequiredRole),
                ],
                ["wrong-role-multi-scope"] =
                [
                    new Claim("jti", "wrong-role-multi-scope"),
                    new Claim("scope", "Alpha Beta"),
                    new Claim(RoleClaimType, "dms-operator-other"),
                ],
            };

            IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions = Options.Create(
                new JwtAuthenticationOptions { RoleClaimType = RoleClaimType }
            );
            ManagementEndpointAuthorizationService management = new(
                _validator,
                Options.Create(new ManagementEndpointsOptions { RequiredRole = RequiredRole }),
                jwtAuthenticationOptions,
                NullLogger<ManagementEndpointAuthorizationService>.Instance
            );
            DocumentCacheStatusAuthorizationService documentCache = new(
                _validator,
                Options.Create(
                    new DocumentCacheOptions
                    {
                        Status = new DocumentCacheStatusOptions { RequiredRole = RequiredRole },
                    }
                ),
                jwtAuthenticationOptions,
                NullLogger<DocumentCacheStatusAuthorizationService>.Instance
            );

            foreach ((string name, Claim[] claims) in tokens)
            {
                string header =
                    $"Bearer {JwtValidationServiceTests.CreateTestToken(claims, authority, Audience, signingKey, tokenHandler)}";
                _managementResults[name] = await management.AuthorizeAsync(header);
                _documentCacheResults[name] = await documentCache.AuthorizeAsync(header);
            }
        }

        [Test]
        public void It_resolves_requests_from_the_cached_record() =>
            _authorizationsSeenByConsumer
                .Should()
                .HaveCount(3)
                .And.AllSatisfy(authorizations => authorizations.Should().BeSameAs(_cachedAuthorizations));

        [Test]
        public void It_keeps_the_jwt_validation_cached_across_the_metadata_change() =>
            _revalidatedAuthorizations.Should().BeSameAs(_cachedAuthorizations);

        [Test]
        public void It_selects_the_one_match_in_the_first_tenant()
        {
            _tenantAFirstContinuations.Should().Be(1);
            _tenantAFirstRequest.FrontendResponse.Should().Be(No.FrontendResponse);
            _tenantAFirstRequest.ClientAuthorizations.ClaimSetName.Should().Be("Alpha");
        }

        [Test]
        public void It_selects_a_different_claim_set_in_the_second_tenant()
        {
            _tenantBContinuations.Should().Be(1);
            _tenantBRequest.FrontendResponse.Should().Be(No.FrontendResponse);
            _tenantBRequest.ClientAuthorizations.ClaimSetName.Should().Be("Beta");
        }

        [Test]
        public void It_forbids_the_request_after_the_tenant_metadata_gains_a_second_match()
        {
            IFrontendResponse response = _tenantAAfterMetadataChangeRequest.FrontendResponse;

            response.StatusCode.Should().Be(403);
            response.ContentType.Should().Be("application/problem+json");
            response.Body!["errors"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Equal("The token identifies multiple claim sets.");
            _tenantAAfterMetadataChangeContinuations.Should().Be(0);
        }

        [Test]
        public void It_does_not_mutate_the_cached_authorizations()
        {
            ClientAuthorizations cachedAuthorizations = _revalidatedAuthorizations;

            cachedAuthorizations.ScopeValues.Should().Equal("profile", "Alpha", "Beta");
            cachedAuthorizations.ClaimSetName.Should().BeEmpty();
        }

        [Test]
        public void It_reads_each_tenants_metadata_once_per_request()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(TenantA, A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(TenantB, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [TestCase("no-scope")]
        [TestCase("multi-scope")]
        public void It_authorizes_the_management_endpoint_on_the_exact_role(string tokenName) =>
            _managementResults[tokenName].Outcome.Should().Be(EndpointRoleAuthorizationOutcome.Authorized);

        [TestCase("no-scope")]
        [TestCase("multi-scope")]
        public void It_authorizes_the_document_cache_status_endpoint_on_the_exact_role(string tokenName) =>
            _documentCacheResults[tokenName]
                .Outcome.Should()
                .Be(DocumentCacheStatusAuthorizationOutcome.Authorized);

        [Test]
        public void It_keeps_a_wrong_role_forbidden_on_role_only_endpoints()
        {
            _managementResults["wrong-role-multi-scope"]
                .Outcome.Should()
                .Be(EndpointRoleAuthorizationOutcome.Forbidden);
            _documentCacheResults["wrong-role-multi-scope"]
                .Outcome.Should()
                .Be(DocumentCacheStatusAuthorizationOutcome.Forbidden);
        }

        [Test]
        public void It_authorizes_role_only_endpoints_without_claim_set_metadata()
        {
            // Only the three identity requests read claim-set metadata.
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustHaveHappened(3, Times.Exactly);
        }
    }
}
