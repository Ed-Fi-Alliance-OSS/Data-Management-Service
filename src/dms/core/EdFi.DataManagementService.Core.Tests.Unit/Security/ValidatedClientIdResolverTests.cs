// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class ValidatedClientIdResolverTests
{
    /// <summary>
    /// A container whose JWT validation service is registered through a factory that counts its
    /// constructions, so a test can tell whether the resolver constructed it at all.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private int _validatorConstructions;

        public Harness(
            bool bypassAuthorization,
            Func<IServiceProvider, IJwtValidationService> createValidator
        )
        {
            ServiceCollection services = new();
            services.AddSingleton<IJwtValidationService>(provider =>
            {
                Interlocked.Increment(ref _validatorConstructions);
                return createValidator(provider);
            });
            _provider = services.BuildServiceProvider();

            Resolver = new ValidatedClientIdResolver(
                _provider,
                Options.Create(
                    new AppSettings
                    {
                        AllowIdentityUpdateOverrides = string.Empty,
                        BypassAuthorization = bypassAuthorization,
                    }
                )
            );
        }

        public ValidatedClientIdResolver Resolver { get; }

        public int ValidatorConstructions => Volatile.Read(ref _validatorConstructions);

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// Builds the real validation service over an OIDC configuration manager whose factory throws, as
    /// production's does when metadata settings are missing or invalid.
    /// </summary>
    private static IJwtValidationService CreateValidatorOverUnconstructibleMetadata(IServiceProvider _) =>
        new JwtValidationService(
            CreateUnconstructibleConfigurationManager(),
            Options.Create(new JwtAuthenticationOptions()),
            NullLogger<JwtValidationService>.Instance
        );

    private static IConfigurationManager<OpenIdConnectConfiguration> CreateUnconstructibleConfigurationManager() =>
        throw new InvalidOperationException("JwtAuthentication:MetadataAddress must be configured");

    [TestFixture]
    [Parallelizable]
    public class Given_A_Header_That_Cannot_Carry_A_Valid_Bearer_Token : ValidatedClientIdResolverTests
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("Basic dXNlcjpwYXNz")]
        [TestCase("Bearer")]
        [TestCase("Bearer ")]
        [TestCase("Bearer a,b")]
        [TestCase("Bearer a b")]
        [TestCase("Bearer\tabc")]
        [TestCase(",Bearer abc")]
        public async Task It_returns_null_without_constructing_jwt_services(string? authorizationHeader)
        {
            using Harness harness = new(
                bypassAuthorization: false,
                CreateValidatorOverUnconstructibleMetadata
            );

            string? clientId = await harness.Resolver.ResolveAsync(
                authorizationHeader,
                CancellationToken.None
            );

            clientId.Should().BeNull();
            harness.ValidatorConstructions.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Bypass_Authorization_And_Unconstructible_Oidc_Metadata : ValidatedClientIdResolverTests
    {
        [Test]
        public async Task It_returns_null_for_a_bearer_token_without_constructing_jwt_services()
        {
            using Harness harness = new(
                bypassAuthorization: true,
                CreateValidatorOverUnconstructibleMetadata
            );

            string? clientId = await harness.Resolver.ResolveAsync(
                "Bearer abc.def.ghi",
                CancellationToken.None
            );

            clientId.Should().BeNull();
            harness.ValidatorConstructions.Should().Be(0);
        }

        [Test]
        public async Task It_would_construct_them_for_the_same_token_without_bypass()
        {
            // The control for the case above: the same token and container without bypass reaches
            // construction, which throws exactly as production's OIDC factory does. Without this, a
            // harness that could never construct the service would make the bypass case vacuous.
            using Harness harness = new(
                bypassAuthorization: false,
                CreateValidatorOverUnconstructibleMetadata
            );

            Func<Task> resolve = () =>
                harness.Resolver.ResolveAsync("Bearer abc.def.ghi", CancellationToken.None);

            await resolve.Should().ThrowAsync<InvalidOperationException>();
            harness.ValidatorConstructions.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_That_Returns_A_Fixed_Outcome : ValidatedClientIdResolverTests
    {
        private static ClientAuthorizations CreateClientAuthorizations(string clientId) =>
            new(
                TokenId: "token-id",
                ClientId: clientId,
                ClaimSetName: "claim-set",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DataStoreIds: []
            );

        private static async Task<(
            string? ClientId,
            Harness Harness,
            IJwtValidationService Validator
        )> Resolve(
            (ClaimsPrincipal?, ClientAuthorizations?) outcome,
            string authorizationHeader = "Bearer the-token"
        )
        {
            IJwtValidationService validator = A.Fake<IJwtValidationService>();
            A.CallTo(() =>
                    validator.ValidateAndExtractClientAuthorizationsAsync(A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(outcome));

            Harness harness = new(bypassAuthorization: false, _ => validator);
            string? clientId = await harness.Resolver.ResolveAsync(
                authorizationHeader,
                CancellationToken.None
            );
            return (clientId, harness, validator);
        }

        [Test]
        public async Task It_passes_only_the_bearer_token_to_the_validator()
        {
            (_, Harness harness, IJwtValidationService validator) = await Resolve(
                (null, null),
                "bearer   the-token"
            );
            using (harness)
            {
                A.CallTo(() =>
                        validator.ValidateAndExtractClientAuthorizationsAsync(
                            "the-token",
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly();
                harness.ValidatorConstructions.Should().Be(1);
            }
        }

        [Test]
        public async Task It_returns_null_for_a_rejected_token()
        {
            (string? clientId, Harness harness, _) = await Resolve((null, null));
            using (harness)
            {
                clientId.Should().BeNull();
            }
        }

        [Test]
        public async Task It_returns_null_for_a_token_without_a_client_id()
        {
            (string? clientId, Harness harness, _) = await Resolve(
                (new ClaimsPrincipal(), CreateClientAuthorizations(string.Empty))
            );
            using (harness)
            {
                clientId.Should().BeNull();
            }
        }

        [Test]
        public async Task It_returns_the_client_id_exactly_as_validated()
        {
            (string? clientId, Harness harness, _) = await Resolve(
                (new ClaimsPrincipal(), CreateClientAuthorizations("Mixed-Case Client_01"))
            );
            using (harness)
            {
                clientId.Should().Be("Mixed-Case Client_01");
            }
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Cancelled_Request : ValidatedClientIdResolverTests
    {
        [Test]
        public async Task It_propagates_the_cancellation_from_validation()
        {
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            IJwtValidationService validator = A.Fake<IJwtValidationService>();
            A.CallTo(() =>
                    validator.ValidateAndExtractClientAuthorizationsAsync("the-token", cancellation.Token)
                )
                .Returns(Task.FromCanceled<(ClaimsPrincipal?, ClientAuthorizations?)>(cancellation.Token));

            using Harness harness = new(bypassAuthorization: false, _ => validator);

            Func<Task> resolve = () => harness.Resolver.ResolveAsync("Bearer the-token", cancellation.Token);

            await resolve.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    /// <summary>
    /// Runs the resolver over the real validation service with a controlled signing key, so the
    /// outcomes below are the production validator's, not a stub's.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Real_Jwt_Validation_Service : ValidatedClientIdResolverTests
    {
        private const string ClientIdClaim = "client_id";

        private Harness _harness = null!;
        private JwtSecurityTokenHandler _tokenHandler = null!;
        private RsaSecurityKey _signingKey = null!;
        private string _issuer = null!;
        private string _audience = null!;

        [SetUp]
        public void Setup()
        {
            var (service, configurationManager, options, _, tokenHandler, signingKey) =
                JwtValidationServiceTests.CreateService();

            _tokenHandler = tokenHandler;
            _signingKey = signingKey;
            _issuer = options.Value.Authority;
            _audience = options.Value.Audience;

            OpenIdConnectConfiguration metadata = new() { Issuer = _issuer };
            metadata.SigningKeys.Add(signingKey);
            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(Task.FromResult(metadata));

            _harness = new Harness(bypassAuthorization: false, _ => service);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        private string CreateToken(
            Claim[] claims,
            string? issuer = null,
            string? audience = null,
            SecurityKey? signingKey = null,
            bool expired = false
        ) =>
            JwtValidationServiceTests.CreateTestToken(
                claims,
                issuer ?? _issuer,
                audience ?? _audience,
                signingKey ?? _signingKey,
                _tokenHandler,
                expired: expired
            );

        private Task<string?> Resolve(string token) =>
            _harness.Resolver.ResolveAsync($"Bearer {token}", CancellationToken.None);

        [Test]
        public async Task It_resolves_two_distinct_valid_tokens_for_one_client_to_the_same_client_id()
        {
            string first = CreateToken([new Claim(ClientIdClaim, "client-a"), new Claim("jti", "token-1")]);
            string second = CreateToken([new Claim(ClientIdClaim, "client-a"), new Claim("jti", "token-2")]);

            first.Should().NotBe(second);
            (await Resolve(first)).Should().Be("client-a");
            (await Resolve(second)).Should().Be("client-a");
        }

        [Test]
        public async Task It_returns_null_for_a_token_signed_by_another_key()
        {
            using RSA otherRsa = RSA.Create(2048);
            string forged = CreateToken(
                [new Claim(ClientIdClaim, "client-a")],
                signingKey: new RsaSecurityKey(otherRsa)
            );

            (await Resolve(forged)).Should().BeNull();
        }

        [Test]
        public async Task It_returns_null_for_an_expired_token()
        {
            string expired = CreateToken([new Claim(ClientIdClaim, "client-a")], expired: true);

            (await Resolve(expired)).Should().BeNull();
        }

        [Test]
        public async Task It_returns_null_for_a_token_for_another_audience()
        {
            string token = CreateToken([new Claim(ClientIdClaim, "client-a")], audience: "another-audience");

            (await Resolve(token)).Should().BeNull();
        }

        [Test]
        public async Task It_returns_null_for_a_token_from_another_issuer()
        {
            string token = CreateToken(
                [new Claim(ClientIdClaim, "client-a")],
                issuer: "https://attacker.example/realms/edfi"
            );

            (await Resolve(token)).Should().BeNull();
        }

        [Test]
        public async Task It_returns_null_for_a_value_that_is_not_a_jwt()
        {
            (await Resolve("not-a-jwt")).Should().BeNull();
        }

        [Test]
        public async Task It_returns_null_for_a_valid_token_without_a_client_id_claim()
        {
            string token = CreateToken([new Claim("jti", "token-without-client")]);

            (await Resolve(token)).Should().BeNull();
        }
    }
}
