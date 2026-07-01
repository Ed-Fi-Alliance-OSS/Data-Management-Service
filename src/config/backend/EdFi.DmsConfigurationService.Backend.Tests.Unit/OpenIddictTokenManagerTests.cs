// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class OpenIddictTokenManagerTests
{
    private IClientSecretHasher _secretHasher = null!;
    private IOpenIddictTokenRepository _tokenRepository = null!;
    private OpenIddictTokenManager _tokenManager = null!;

    [SetUp]
    public void Setup()
    {
        _secretHasher = A.Fake<IClientSecretHasher>();
        _tokenRepository = A.Fake<IOpenIddictTokenRepository>();

        _tokenManager = new OpenIddictTokenManager(
            Options.Create(new IdentityOptions()),
            NullLogger<OpenIddictTokenManager>.Instance,
            _secretHasher,
            _tokenRepository
        );
    }

    private const string TestIssuer = "https://cms.example.test";
    private const string TestAudience = "ed-fi-cms-tests";

    /// <summary>
    /// Builds a token manager configured with the test issuer/audience so that
    /// ValidateTokenAsync can verify tokens produced by the helpers below.
    /// </summary>
    private OpenIddictTokenManager CreateConfiguredTokenManager() =>
        new(
            Options.Create(new IdentityOptions { Authority = TestIssuer, Audience = TestAudience }),
            NullLogger<OpenIddictTokenManager>.Instance,
            _secretHasher,
            _tokenRepository
        );

    /// <summary>
    /// Creates an RSA signing key plus the matching public key bytes (SubjectPublicKeyInfo)
    /// that the faked repository returns from GetActivePublicKeysAsync.
    /// </summary>
    private static (string KeyId, byte[] PublicKeySpki, RsaSecurityKey SigningKey) CreateSigningKey()
    {
        var rsa = RSA.Create(2048);
        string keyId = Guid.NewGuid().ToString();
        byte[] publicKeySpki = rsa.ExportSubjectPublicKeyInfo();
        var signingKey = new RsaSecurityKey(rsa) { KeyId = keyId };
        return (keyId, publicKeySpki, signingKey);
    }

    /// <summary>
    /// Issues a signed JWT with the test issuer/audience and the supplied claims. The "kid"
    /// header is emitted from the signing key so the validator can resolve the public key.
    /// </summary>
    private static string CreateSignedToken(
        RsaSecurityKey signingKey,
        IEnumerable<Claim> claims,
        bool expired = false
    )
    {
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            notBefore: expired ? now.AddMinutes(-15) : now.AddMinutes(-5),
            expires: expired ? now.AddMinutes(-10) : now.AddMinutes(10),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        );
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenApiClientIsNotApproved : OpenIddictTokenManagerTests
    {
        [SetUp]
        public void Arrange()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("disabled-client"))
                .Returns(
                    new ApplicationInfo
                    {
                        ClientId = "disabled-client",
                        ClientSecret = "hashed-secret",
                        IsApproved = false,
                    }
                );

            A.CallTo(() => _secretHasher.VerifySecretAsync("plain-secret", "hashed-secret")).Returns(true);
        }

        [Test]
        public async Task It_should_return_failure_identity_provider_invalid_client_when_application_is_not_approved()
        {
            var credentials = new List<KeyValuePair<string, string>>
            {
                new("client_id", "disabled-client"),
                new("client_secret", "plain-secret"),
            };

            var result = await _tokenManager.GetAccessTokenAsync(credentials);

            result.Should().BeOfType<TokenResult.FailureIdentityProvider>();
            var failure = (TokenResult.FailureIdentityProvider)result;
            failure.IdentityProviderError.Should().BeOfType<IdentityProviderError.InvalidClient>();
        }
    }

    // The self-contained provider re-checks token status by jti on every request after
    // standard validation, so a revoked token is rejected on reuse. This is the platform's
    // strongest replay control. The fixtures below pin that behavior.

    // A valid self-contained token is reusable for the life of its "valid" status: the
    // status check does not consume the token, so repeated presentations all succeed.
    // This distinguishes the design from a one-time-use token.
    [TestFixture]
    public class Given_ValidateTokenAsync_WithAValidStoredToken : OpenIddictTokenManagerTests
    {
        private readonly List<bool> _results = [];

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            var jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti)).Returns("valid");

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()) }
            );

            var manager = CreateConfiguredTokenManager();

            // NUnit reuses a single fixture instance and runs SetUp before each test, so
            // reset before repopulating to avoid accumulation across tests.
            _results.Clear();

            // Present the same token several times to prove it is reusable while valid.
            for (int i = 0; i < 3; i++)
            {
                _results.Add(await manager.ValidateTokenAsync(token));
            }
        }

        [Test]
        public void It_accepts_the_token_on_every_presentation()
        {
            _results.Should().HaveCount(3);
            _results.Should().AllBeEquivalentTo(true);
        }
    }

    [TestFixture]
    public class Given_ValidateTokenAsync_WithARevokedToken : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            var jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti)).Returns("revoked");

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()) }
            );

            _result = await CreateConfiguredTokenManager().ValidateTokenAsync(token);
        }

        [Test]
        public void It_rejects_the_revoked_token()
        {
            _result.Should().BeFalse();
        }
    }

    // Replayability lasts only until expiry: the lifetime check runs before the status
    // lookup, so an expired token is rejected even when its stored status is "valid".
    [TestFixture]
    public class Given_ValidateTokenAsync_WithAnExpiredToken : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            // A "valid" status must not rescue an expired token.
            var jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti)).Returns("valid");

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()) },
                expired: true
            );

            _result = await CreateConfiguredTokenManager().ValidateTokenAsync(token);
        }

        [Test]
        public void It_rejects_the_expired_token()
        {
            _result.Should().BeFalse();
        }

        [Test]
        public void It_does_not_reach_the_status_check()
        {
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_ValidateTokenAsync_WithAnUnknownJti : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            // No stored status for this jti -> repository returns null.
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).Returns((string?)null);

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) }
            );

            _result = await CreateConfiguredTokenManager().ValidateTokenAsync(token);
        }

        [Test]
        public void It_rejects_a_token_whose_jti_is_unknown()
        {
            _result.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_ValidateTokenAsync_WithoutAJti : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            string token = CreateSignedToken(signingKey, Array.Empty<Claim>());

            _result = await CreateConfiguredTokenManager().ValidateTokenAsync(token);
        }

        [Test]
        public void It_rejects_a_token_without_a_jti()
        {
            _result.Should().BeFalse();
        }

        [Test]
        public void It_does_not_query_token_status()
        {
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_ValidateTokenAsync_WithAMalformedJti : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, "not-a-valid-guid") }
            );

            _result = await CreateConfiguredTokenManager().ValidateTokenAsync(token);
        }

        [Test]
        public void It_rejects_a_token_with_a_malformed_jti()
        {
            _result.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithAValidJti : OpenIddictTokenManagerTests
    {
        private bool _result;
        private Guid _jti;

        [SetUp]
        public async Task Act()
        {
            var (_, _, signingKey) = CreateSigningKey();
            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, _jti.ToString()) }
            );

            _result = await _tokenManager.RevokeTokenAsync(token);
        }

        [Test]
        public void It_returns_true()
        {
            _result.Should().BeTrue();
        }

        [Test]
        public void It_revokes_the_token_by_jti()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithoutAJti : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (_, _, signingKey) = CreateSigningKey();
            string token = CreateSignedToken(signingKey, Array.Empty<Claim>());

            _result = await _tokenManager.RevokeTokenAsync(token);
        }

        [Test]
        public void It_returns_false()
        {
            _result.Should().BeFalse();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithAMalformedJti : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (_, _, signingKey) = CreateSigningKey();
            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, "not-a-valid-guid") }
            );

            _result = await _tokenManager.RevokeTokenAsync(token);
        }

        [Test]
        public void It_returns_false()
        {
            _result.Should().BeFalse();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }
}
