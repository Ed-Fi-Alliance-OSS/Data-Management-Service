// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

[TestFixture]
[Parallelizable]
public class JwtValidationServiceTests
{
    internal static (
        JwtValidationService service,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IOptions<JwtAuthenticationOptions> options,
        ILogger<JwtValidationService> logger,
        JwtSecurityTokenHandler tokenHandler,
        RsaSecurityKey signingKey
    ) CreateService(
        Action<JwtAuthenticationOptions>? configureOptions = null,
        TimeProvider? timeProvider = null
    )
    {
        var configurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
        var options = A.Fake<IOptions<JwtAuthenticationOptions>>();
        var logger = A.Fake<ILogger<JwtValidationService>>();
        var tokenHandler = new JwtSecurityTokenHandler();

        // Create RSA key for testing
        var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);

        var jwtOptions = new JwtAuthenticationOptions
        {
            Audience = "ed-fi-ods-api",
            ClockSkewSeconds = 30,
            RoleClaimType = "role",
        };
        configureOptions?.Invoke(jwtOptions);
        A.CallTo(() => options.Value).Returns(jwtOptions);

        var service = new JwtValidationService(configurationManager, options, logger, timeProvider);

        return (service, configurationManager, options, logger, tokenHandler, signingKey);
    }

    private static string CreateTestToken(
        Claim[] claims,
        string issuer,
        string audience,
        RsaSecurityKey signingKey,
        JwtSecurityTokenHandler tokenHandler,
        bool expired = false,
        DateTime? nowUtc = null,
        TimeSpan? validFor = null
    )
    {
        var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

        var now = nowUtc ?? DateTime.UtcNow;
        var expires = expired ? now.AddMinutes(-10) : now.Add(validFor ?? TimeSpan.FromMinutes(10));
        var notBefore = expired ? now.AddMinutes(-15) : now.AddMinutes(-5);

        var jwt = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: signingCredentials
        );

        return tokenHandler.WriteToken(jwt);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_With_All_Claims : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[]
            {
                new Claim("jti", "test-token-id"),
                new Claim("scope", "edfi-admin"),
                new Claim("namespacePrefixes", "uri://ed-fi.org,uri://tpdm.ed-fi.org"),
                new Claim("educationOrganizationIds", "123,456"),
                new Claim("dataStoreIds", "1,2"),
            };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_a_valid_principal()
        {
            _principal.Should().NotBeNull();
        }

        [Test]
        public void It_returns_client_authorizations()
        {
            _clientAuthorizations.Should().NotBeNull();
        }

        [Test]
        public void It_extracts_the_token_id()
        {
            _clientAuthorizations!.TokenId.Should().Be("test-token-id");
        }

        [Test]
        public void It_extracts_the_claim_set_name()
        {
            _clientAuthorizations!.ClaimSetName.Should().Be("edfi-admin");
        }

        [Test]
        public void It_extracts_namespace_prefixes()
        {
            _clientAuthorizations!.NamespacePrefixes.Should().HaveCount(2);
            _clientAuthorizations.NamespacePrefixes[0].Value.Should().Be("uri://ed-fi.org");
            _clientAuthorizations.NamespacePrefixes[1].Value.Should().Be("uri://tpdm.ed-fi.org");
        }

        [Test]
        public void It_extracts_education_organization_ids()
        {
            _clientAuthorizations!.EducationOrganizationIds.Should().HaveCount(2);
            _clientAuthorizations.EducationOrganizationIds[0].Value.Should().Be(123);
            _clientAuthorizations.EducationOrganizationIds[1].Value.Should().Be(456);
        }

        [Test]
        public void It_extracts_data_store_ids()
        {
            _clientAuthorizations!.DataStoreIds.Should().HaveCount(2);
            _clientAuthorizations.DataStoreIds[0].Value.Should().Be(1);
            _clientAuthorizations.DataStoreIds[1].Value.Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Expired_Token : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("jti", "test-token-id"), new Claim("scope", "edfi-admin") };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler,
                expired: true
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_null_principal()
        {
            _principal.Should().BeNull();
        }

        [Test]
        public void It_returns_null_client_authorizations()
        {
            _clientAuthorizations.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Token_With_Invalid_Signature : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            // Use different key for validation than signing
            var differentRsa = RSA.Create(2048);
            oidcConfig.SigningKeys.Add(new RsaSecurityKey(differentRsa));

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("jti", "test-token-id"), new Claim("scope", "edfi-admin") };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_null_principal()
        {
            _principal.Should().BeNull();
        }

        [Test]
        public void It_returns_null_client_authorizations()
        {
            _clientAuthorizations.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_With_Missing_Claims : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            // Token with minimal claims
            var claims = Array.Empty<Claim>();
            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_a_valid_principal()
        {
            _principal.Should().NotBeNull();
        }

        [Test]
        public void It_returns_client_authorizations_with_defaults()
        {
            _clientAuthorizations.Should().NotBeNull();
        }

        [Test]
        public void It_generates_a_token_id_from_hash()
        {
            _clientAuthorizations!.TokenId.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void It_uses_empty_claim_set_name()
        {
            _clientAuthorizations!.ClaimSetName.Should().BeEmpty();
        }

        [Test]
        public void It_uses_empty_namespace_prefixes()
        {
            _clientAuthorizations!.NamespacePrefixes.Should().BeEmpty();
        }

        [Test]
        public void It_uses_empty_education_organization_ids()
        {
            _clientAuthorizations!.EducationOrganizationIds.Should().BeEmpty();
        }

        [Test]
        public void It_uses_empty_data_store_ids()
        {
            _clientAuthorizations!.DataStoreIds.Should().BeEmpty();
        }
    }

    /// <summary>
    /// DMS performs stateless self-inspection of bearer tokens and does not maintain
    /// a replay cache or perform per-request revocation. The same valid token may be
    /// presented repeatedly within its lifetime and is accepted every time.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_Validated_Repeatedly : JwtValidationServiceTests
    {
        private readonly List<(
            ClaimsPrincipal? Principal,
            ClientAuthorizations? ClientAuthorizations
        )> _results = [];

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("jti", "replayed-token-id"), new Claim("scope", "edfi-admin") };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // NUnit reuses a single fixture instance across the fixture's tests and runs
            // SetUp before each, so reset before repopulating to avoid accumulation.
            _results.Clear();

            // Act - present the same token several times
            for (int i = 0; i < 3; i++)
            {
                _results.Add(
                    await service.ValidateAndExtractClientAuthorizationsAsync(token, CancellationToken.None)
                );
            }
        }

        [Test]
        public void It_accepts_the_token_on_every_presentation()
        {
            _results.Should().HaveCount(3);
            _results
                .Should()
                .AllSatisfy(r =>
                {
                    r.Principal.Should().NotBeNull();
                    r.ClientAuthorizations.Should().NotBeNull();
                });
        }

        [Test]
        public void It_returns_the_same_token_id_each_time()
        {
            _results.Select(r => r.ClientAuthorizations!.TokenId).Should().AllBe("replayed-token-id");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Authorization_Header_Validated_Repeatedly : JwtValidationServiceTests
    {
        private JwtValidationService _service = null!;
        private ClaimsPrincipal? _firstPrincipal = null;
        private ClaimsPrincipal? _secondPrincipal = null;
        private ClientAuthorizations? _firstClientAuthorizations = null;
        private ClientAuthorizations? _secondClientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var fakeTimeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
            );
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService(
                o =>
                {
                    o.ClockSkewSeconds = 0;
                    o.ValidatedTokenCacheMaxEntries = 4;
                },
                fakeTimeProvider
            );
            _service = service;
            signingKey.KeyId = "key-1";

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("jti", "cached-token-id"), new Claim("scope", "edfi-admin") };
            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler,
                nowUtc: fakeTimeProvider.GetUtcNow().UtcDateTime
            );

            string authorizationHeader = $"Bearer {token}";

            (_firstPrincipal, _firstClientAuthorizations) =
                await service.ValidateAndExtractClientAuthorizationsAsync(
                    authorizationHeader,
                    "Bearer ".Length,
                    CancellationToken.None
                );
            (_secondPrincipal, _secondClientAuthorizations) =
                await service.ValidateAndExtractClientAuthorizationsAsync(
                    authorizationHeader,
                    "Bearer ".Length,
                    CancellationToken.None
                );
        }

        [Test]
        public void It_accepts_each_presentation()
        {
            _firstPrincipal.Should().NotBeNull();
            _secondPrincipal.Should().NotBeNull();
            _firstClientAuthorizations.Should().NotBeNull();
            _secondClientAuthorizations.Should().NotBeNull();
        }

        [Test]
        public void It_caches_the_successful_validation()
        {
            _service.ValidatedTokenCacheCount.Should().Be(1);
        }

        [Test]
        public void It_does_not_share_the_same_principal_instance_on_cache_hit()
        {
            _secondPrincipal.Should().NotBeSameAs(_firstPrincipal);
        }

        [Test]
        public void It_reuses_the_cached_client_authorizations()
        {
            _secondClientAuthorizations.Should().BeSameAs(_firstClientAuthorizations);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Token_With_Caching_Enabled : JwtValidationServiceTests
    {
        private JwtValidationService _service = null!;
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService(o =>
                o.ValidatedTokenCacheMaxEntries = 4
            );
            _service = service;

            var validationKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "validation-key" };
            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(validationKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var token = CreateTestToken(
                [new Claim("jti", "invalid-token-id")],
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_rejects_the_token()
        {
            _principal.Should().BeNull();
            _clientAuthorizations.Should().BeNull();
        }

        [Test]
        public void It_does_not_cache_the_failed_validation()
        {
            _service.ValidatedTokenCacheCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validated_Token_Cache_With_A_One_Entry_Limit : JwtValidationServiceTests
    {
        private JwtValidationService _service = null!;

        [SetUp]
        public async Task Setup()
        {
            var fakeTimeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
            );
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService(
                o =>
                {
                    o.ClockSkewSeconds = 0;
                    o.ValidatedTokenCacheMaxEntries = 1;
                },
                fakeTimeProvider
            );
            _service = service;
            signingKey.KeyId = "key-1";

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            string firstToken = CreateTestToken(
                [new Claim("jti", "first-token-id")],
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler,
                nowUtc: fakeTimeProvider.GetUtcNow().UtcDateTime
            );
            fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
            string secondToken = CreateTestToken(
                [new Claim("jti", "second-token-id")],
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler,
                nowUtc: fakeTimeProvider.GetUtcNow().UtcDateTime
            );

            await service.ValidateAndExtractClientAuthorizationsAsync(firstToken, CancellationToken.None);
            await service.ValidateAndExtractClientAuthorizationsAsync(secondToken, CancellationToken.None);
        }

        [Test]
        public void It_prunes_to_the_configured_entry_limit()
        {
            _service.ValidatedTokenCacheCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Cached_Token_After_Its_Expiration : JwtValidationServiceTests
    {
        private JwtValidationService _service = null!;
        private ClaimsPrincipal? _firstPrincipal = null;
        private ClaimsPrincipal? _secondPrincipal = null;
        private ClientAuthorizations? _secondClientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var fakeTimeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
            );
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService(
                o =>
                {
                    o.ClockSkewSeconds = 0;
                    o.ValidatedTokenCacheMaxEntries = 4;
                    o.ValidatedTokenCacheEntryMaxLifetimeSeconds = 300;
                },
                fakeTimeProvider
            );
            _service = service;
            signingKey.KeyId = "key-1";

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            string token = CreateTestToken(
                [new Claim("jti", "short-lived-token-id")],
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler,
                nowUtc: fakeTimeProvider.GetUtcNow().UtcDateTime,
                validFor: TimeSpan.FromSeconds(2)
            );

            (_firstPrincipal, _) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );

            fakeTimeProvider.Advance(TimeSpan.FromSeconds(3));

            (_secondPrincipal, _secondClientAuthorizations) =
                await service.ValidateAndExtractClientAuthorizationsAsync(token, CancellationToken.None);
        }

        [Test]
        public void It_accepts_the_token_before_expiration()
        {
            _firstPrincipal.Should().NotBeNull();
        }

        [Test]
        public void It_does_not_accept_the_expired_token_from_cache()
        {
            _secondPrincipal.Should().BeNull();
            _secondClientAuthorizations.Should().BeNull();
        }

        [Test]
        public void It_removes_the_expired_cache_entry()
        {
            _service.ValidatedTokenCacheCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Cached_Token_After_The_Signing_Key_Changes : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _firstPrincipal = null;
        private ClaimsPrincipal? _secondPrincipal = null;
        private ClientAuthorizations? _secondClientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var fakeTimeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
            );
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService(
                o =>
                {
                    o.ClockSkewSeconds = 0;
                    o.ValidatedTokenCacheMaxEntries = 4;
                },
                fakeTimeProvider
            );
            signingKey.KeyId = "key-1";

            var firstConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            firstConfig.SigningKeys.Add(signingKey);

            var secondConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            secondConfig.SigningKeys.Add(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "key-1" });

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .ReturnsNextFromSequence(Task.FromResult(firstConfig), Task.FromResult(secondConfig));

            string token = CreateTestToken(
                [new Claim("jti", "key-rollover-token-id")],
                firstConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler,
                nowUtc: fakeTimeProvider.GetUtcNow().UtcDateTime
            );

            (_firstPrincipal, _) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
            (_secondPrincipal, _secondClientAuthorizations) =
                await service.ValidateAndExtractClientAuthorizationsAsync(token, CancellationToken.None);
        }

        [Test]
        public void It_accepts_the_token_with_the_original_signing_key()
        {
            _firstPrincipal.Should().NotBeNull();
        }

        [Test]
        public void It_misses_the_cache_and_rejects_the_token_with_the_new_signing_key()
        {
            _secondPrincipal.Should().BeNull();
            _secondClientAuthorizations.Should().BeNull();
        }
    }

    /// <summary>
    /// The jti claim is informational for DMS: it is copied to TokenId for correlation
    /// but is never parsed or used in the accept/reject decision. A malformed (non-GUID)
    /// jti therefore does not cause rejection.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_With_Malformed_Jti : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("jti", "not-a-valid-guid-###"), new Claim("scope", "edfi-admin") };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_a_valid_principal()
        {
            _principal.Should().NotBeNull();
        }

        [Test]
        public void It_stores_the_malformed_jti_verbatim_as_the_token_id()
        {
            _clientAuthorizations!.TokenId.Should().Be("not-a-valid-guid-###");
        }
    }

    /// <summary>
    /// DMS does not require a jti claim. When it is absent the token is still accepted
    /// and TokenId falls back to a derived value (a hash of the raw token) so that
    /// log correlation still has a non-empty identifier to key on.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Token_Without_Jti : JwtValidationServiceTests
    {
        private ClaimsPrincipal? _principal = null;
        private ClientAuthorizations? _clientAuthorizations = null;

        [SetUp]
        public async Task Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) = CreateService();

            var oidcConfig = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.example.com/realms/edfi",
            };
            oidcConfig.SigningKeys.Add(signingKey);

            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(oidcConfig));

            var claims = new[] { new Claim("scope", "edfi-admin") };

            var token = CreateTestToken(
                claims,
                oidcConfig.Issuer,
                options.Value.Audience,
                signingKey,
                tokenHandler
            );

            // Act
            (_principal, _clientAuthorizations) = await service.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_a_valid_principal()
        {
            _principal.Should().NotBeNull();
        }

        [Test]
        public void It_falls_back_to_a_derived_non_empty_token_id()
        {
            _clientAuthorizations!.TokenId.Should().NotBeNullOrEmpty();
        }
    }
}
