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
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    private OpenIddictTokenManager CreateConfiguredTokenManager(
        ILogger<OpenIddictTokenManager>? logger = null
    ) =>
        new(
            Options.Create(new IdentityOptions { Authority = TestIssuer, Audience = TestAudience }),
            logger ?? NullLogger<OpenIddictTokenManager>.Instance,
            _secretHasher,
            _tokenRepository
        );

    /// <summary>
    /// Counts entries the manager wrote at a given level. The logging extension methods funnel
    /// into <see cref="ILogger.Log{TState}"/>, whose first argument is the level, so intercepting
    /// that one method observes every call regardless of which extension produced it.
    /// </summary>
    private static int LogCountAt(ILogger<OpenIddictTokenManager> logger, LogLevel level) =>
        Fake.GetCalls(logger)
            .Count(call => call.Method.Name == nameof(ILogger.Log) && call.GetArgument<LogLevel>(0) == level);

    /// <summary>
    /// The formatted messages the manager wrote at a given level. Argument 2 of
    /// <see cref="ILogger.Log{TState}"/> is the state object, whose ToString() renders the
    /// message template with its values substituted.
    /// </summary>
    private static IReadOnlyList<string> LogMessagesAt(
        ILogger<OpenIddictTokenManager> logger,
        LogLevel level
    ) =>
        Fake.GetCalls(logger)
            .Where(call => call.Method.Name == nameof(ILogger.Log) && call.GetArgument<LogLevel>(0) == level)
            .Select(call => call.Arguments[2]?.ToString() ?? string.Empty)
            .ToList();

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
        bool expired = false,
        string? issuer = null,
        string? audience = null
    )
    {
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(
            issuer: issuer ?? TestIssuer,
            audience: audience ?? TestAudience,
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
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
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

            var credentials = new List<KeyValuePair<string, string>>
            {
                new("client_id", "disabled-client"),
                new("client_secret", "plain-secret"),
            };

            _result = await _tokenManager.GetAccessTokenAsync(credentials);
        }

        [Test]
        public void It_returns_an_invalid_client_authentication_failure() =>
            _result
                .Should()
                .BeEquivalentTo(
                    new TokenResult.FailureAuthentication(
                        "invalid_client",
                        "Invalid client or Invalid client credentials"
                    )
                );
    }

    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenClientIsUnknown : OpenIddictTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("unknown-client"))
                .Returns((ApplicationInfo?)null);

            var credentials = new List<KeyValuePair<string, string>>
            {
                new("client_id", "unknown-client"),
                new("client_secret", "plain-secret"),
            };

            _result = await _tokenManager.GetAccessTokenAsync(credentials);
        }

        [Test]
        public void It_returns_an_invalid_client_authentication_failure() =>
            _result
                .Should()
                .BeEquivalentTo(
                    new TokenResult.FailureAuthentication(
                        "invalid_client",
                        "Invalid client or Invalid client credentials"
                    )
                );
    }

    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenClientSecretIsInvalid : OpenIddictTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("known-client"))
                .Returns(
                    new ApplicationInfo
                    {
                        ClientId = "known-client",
                        ClientSecret = "hashed-secret",
                        IsApproved = true,
                    }
                );

            A.CallTo(() => _secretHasher.VerifySecretAsync("wrong-secret", "hashed-secret")).Returns(false);

            var credentials = new List<KeyValuePair<string, string>>
            {
                new("client_id", "known-client"),
                new("client_secret", "wrong-secret"),
            };

            _result = await _tokenManager.GetAccessTokenAsync(credentials);
        }

        [Test]
        public void It_returns_an_unauthorized_client_authentication_failure() =>
            _result
                .Should()
                .BeEquivalentTo(
                    new TokenResult.FailureAuthentication(
                        "unauthorized_client",
                        "Invalid client or Invalid client credentials"
                    )
                );
    }

    // AuthenticateClientAsync authenticates an RFC 7009 revocation caller using the same
    // application lookup and secret-hash comparison as GetAccessTokenAsync above, so these
    // fixtures pin that it agrees with those three on what counts as a valid client secret.
    // It returns the stored canonical client id rather than a bare flag, because that is the
    // value tokens are minted from and therefore the only value the ownership check can use.

    // Client authentication now happens inside RevokeTokenAsync (DMS-1327 D-02): the caller's
    // credentials are checked before the target token is looked at, so an unknown or invalid
    // token can never hide an authentication failure. The fixtures below pin that ordering and
    // the canonical-client-id rule DMS-1478 introduced.

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenClientIsUnknown : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("unknown-client"))
                .Returns((ApplicationInfo?)null);

            _result = await CreateConfiguredTokenManager()
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        "unknown-client",
                        "plain-secret",
                        "any-token",
                        TokenTypeHint.None
                    ),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_invalid_client() =>
            _result.Should().BeOfType<TokenRevocationResult.InvalidClient>();

        [Test]
        public void It_does_not_load_verification_keys() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync()).MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenSecretIsInvalid : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("known-client"))
                .Returns(
                    new ApplicationInfo
                    {
                        ClientId = "known-client",
                        ClientSecret = "hashed-secret",
                        IsApproved = true,
                    }
                );
            A.CallTo(() => _secretHasher.VerifySecretAsync("wrong-secret", "hashed-secret")).Returns(false);

            _result = await CreateConfiguredTokenManager()
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        "known-client",
                        "wrong-secret",
                        "any-token",
                        TokenTypeHint.None
                    ),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_invalid_client() =>
            _result.Should().BeOfType<TokenRevocationResult.InvalidClient>();

        [Test]
        public void It_does_not_load_verification_keys() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync()).MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenApiClientIsNotApproved : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
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

            _result = await CreateConfiguredTokenManager()
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        "disabled-client",
                        "plain-secret",
                        "any-token",
                        TokenTypeHint.None
                    ),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_invalid_client() =>
            _result.Should().BeOfType<TokenRevocationResult.InvalidClient>();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithMissingCredentials : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act() =>
            _result = await CreateConfiguredTokenManager()
                .RevokeTokenAsync(
                    new TokenRevocationRequest(string.Empty, string.Empty, "any-token", TokenTypeHint.None),
                    CancellationToken.None
                );

        [Test]
        public void It_reports_invalid_client() =>
            _result.Should().BeOfType<TokenRevocationResult.InvalidClient>();

        [Test]
        public void It_does_not_query_the_repository() =>
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(A<string>._)).MustNotHaveHappened();
    }

    // The case that motivated the canonical client id (DMS-1478): SQL Server's default collation
    // resolves a mis-cased client id, so the caller authenticates under a spelling that was never
    // stored. The ownership comparison must see the canonical value the token was minted from,
    // or the owner's own token would look like a stranger's.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithNonCanonicalCasing : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private Guid _jti;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            // Stands in for a case-insensitive collation: the lookup resolves the mis-cased id
            // to the application registered under the canonical one.
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("KNOWN-Client"))
                .Returns(
                    new ApplicationInfo
                    {
                        ClientId = "known-client",
                        ClientSecret = "hashed-secret",
                        IsApproved = true,
                    }
                );
            A.CallTo(() => _secretHasher.VerifySecretAsync("plain-secret", "hashed-secret")).Returns(true);

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);
            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, _jti.ToString()),
                    new Claim("client_id", "known-client"),
                }
            );

            _result = await CreateConfiguredTokenManager()
                .RevokeTokenAsync(
                    new TokenRevocationRequest("KNOWN-Client", "plain-secret", token, TokenTypeHint.None),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_completed() => _result.Should().BeOfType<TokenRevocationResult.Completed>();

        [Test]
        public void It_revokes_the_token_minted_under_the_canonical_id() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
    }

    // A stored row with no client id cannot yield a canonical value, and an empty one would put
    // the caller into an ownership check no token can ever match. The requested spelling is used
    // instead, matching what minting does in the same situation.
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheStoredClientIdIsEmpty : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private Guid _jti;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("known-client"))
                .Returns(
                    new ApplicationInfo
                    {
                        ClientId = string.Empty,
                        ClientSecret = "hashed-secret",
                        IsApproved = true,
                    }
                );
            A.CallTo(() => _secretHasher.VerifySecretAsync("plain-secret", "hashed-secret")).Returns(true);

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);
            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, _jti.ToString()),
                    new Claim("client_id", "known-client"),
                }
            );

            _result = await CreateConfiguredTokenManager()
                .RevokeTokenAsync(
                    new TokenRevocationRequest("known-client", "plain-secret", token, TokenTypeHint.None),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_completed() => _result.Should().BeOfType<TokenRevocationResult.Completed>();

        [Test]
        public void It_falls_back_to_the_requested_client_id_for_ownership() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
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

    // Revocation is ownership-checked: a client may only revoke tokens carrying its own
    // client_id, and the target token's signature is verified before that claim is trusted.
    // The fixtures below pin both halves of that behavior. They configure the issuer/audience
    // and register the signing key's public half, because revocation now runs the same
    // signature/issuer/audience verification the introspection path uses.

    private const string OwnerClientId = "owner-client";
    private const string OtherClientId = "other-client";

    /// <summary>
    /// Registers the supplied public key as the only active key, so tokens signed with its
    /// private half pass verification.
    /// </summary>
    private const string CanonicalClientId = "acme-client";
    private const string NonCanonicalClientId = "Acme-Client";
    private const string TestEncryptionKey = "TestEncryptionKey32CharactersLong1";

    /// <summary>
    /// Stubs one RSA key pair as both the active signing key (used when minting) and the active
    /// public key (used when verifying), so a token minted through GetAccessTokenAsync can be
    /// handed straight back to RevokeTokenAsync in the same test.
    /// </summary>
    private void StubActiveKeyPair()
    {
        var rsa = RSA.Create(2048);
        string keyId = Guid.NewGuid().ToString();

        A.CallTo(() => _tokenRepository.GetActivePrivateKeyAsync(TestEncryptionKey))
            .Returns(
                new PrivateKeyInfo
                {
                    KeyId = keyId,
                    PrivateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
                }
            );

        A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
            .Returns(
                new[]
                {
                    new PublicKeyInfo { KeyId = keyId, PublicKey = rsa.ExportSubjectPublicKeyInfo() },
                }
            );
    }

    /// <summary>
    /// A token manager that can actually mint, i.e. one that can reach a signing key.
    /// </summary>
    private OpenIddictTokenManager CreateMintingTokenManager() =>
        new(
            Options.Create(
                new IdentityOptions
                {
                    Authority = TestIssuer,
                    Audience = TestAudience,
                    EncryptionKey = TestEncryptionKey,
                }
            ),
            NullLogger<OpenIddictTokenManager>.Instance,
            _secretHasher,
            _tokenRepository
        );

    /// <summary>
    /// Registers a client whose stored (canonical) id is <see cref="CanonicalClientId"/> and which
    /// authenticates successfully under whatever casing the caller supplies.
    /// </summary>
    private void StubClientRegisteredAs(string suppliedClientId)
    {
        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(suppliedClientId))
            .Returns(
                new ApplicationInfo
                {
                    Id = Guid.NewGuid(),
                    ClientId = CanonicalClientId,
                    ClientSecret = "hashed-secret",
                    IsApproved = true,
                    Permissions = ["edfi_admin_api/full_access"],
                }
            );

        A.CallTo(() => _secretHasher.VerifySecretAsync("plain-secret", "hashed-secret")).Returns(true);
    }

    /// <summary>
    /// Mints an access token through the real GetAccessTokenAsync path and returns the raw JWT.
    /// </summary>
    private async Task<string> MintAccessTokenAsAsync(string suppliedClientId)
    {
        StubClientRegisteredAs(suppliedClientId);

        var result = await CreateMintingTokenManager()
            .GetAccessTokenAsync([
                new KeyValuePair<string, string>("client_id", suppliedClientId),
                new KeyValuePair<string, string>("client_secret", "plain-secret"),
            ]);

        string payload = ((TokenResult.Success)result).Token;
        return System
            .Text.Json.JsonDocument.Parse(payload)
            .RootElement.GetProperty("access_token")
            .GetString()!;
    }

    private void StubActivePublicKey(string keyId, byte[] publicKeySpki) =>
        A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
            .Returns(
                new[]
                {
                    new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                }
            );

    private const string CallerSecret = "plain-secret";

    /// <summary>
    /// Registers <paramref name="clientId"/> as an approved application whose secret the faked
    /// hasher accepts, so a revocation request carrying <see cref="CallerSecret"/> authenticates.
    /// </summary>
    private void StubAuthenticatedCaller(string clientId)
    {
        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(clientId))
            .Returns(
                new ApplicationInfo
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    ClientSecret = "hashed-secret",
                    IsApproved = true,
                }
            );
        A.CallTo(() => _secretHasher.VerifySecretAsync(CallerSecret, "hashed-secret")).Returns(true);
    }

    private static TokenRevocationRequest RevocationRequestFor(string callerClientId, string token) =>
        new(callerClientId, CallerSecret, token, TokenTypeHint.None);

    /// <summary>
    /// Authenticates <paramref name="callerClientId"/> (unless empty) and asks the manager to
    /// revoke <paramref name="token"/> on its behalf.
    /// </summary>
    private Task<TokenRevocationResult> RevokeAsAsync(
        OpenIddictTokenManager manager,
        string callerClientId,
        string token
    )
    {
        if (callerClientId.Length > 0)
        {
            StubAuthenticatedCaller(callerClientId);
        }

        return manager.RevokeTokenAsync(RevocationRequestFor(callerClientId, token), CancellationToken.None);
    }

    /// <summary>
    /// Everything the manager handed to the logger, at every level: the rendered message, each
    /// structured value, and any attached exception's full text. A disclosure assertion walks all
    /// of it rather than only the rendered message (DMS-1327 D-15).
    /// </summary>
    private static string AllLoggedText(ILogger<OpenIddictTokenManager> logger)
    {
        var text = new System.Text.StringBuilder();
        foreach (var call in Fake.GetCalls(logger).Where(call => call.Method.Name == nameof(ILogger.Log)))
        {
            text.AppendLine(call.Arguments[2]?.ToString());
            if (call.Arguments[2] is IEnumerable<KeyValuePair<string, object?>> state)
            {
                foreach (var pair in state)
                {
                    text.AppendLine(pair.Value?.ToString());
                }
            }

            text.AppendLine((call.Arguments[3] as Exception)?.ToString());
        }

        return text.ToString();
    }

    /// <summary>The exception objects attached to log entries; the revocation path must attach none.</summary>
    private static IReadOnlyList<Exception> LoggedExceptions(ILogger<OpenIddictTokenManager> logger) =>
        Fake.GetCalls(logger)
            .Where(call => call.Method.Name == nameof(ILogger.Log))
            .Select(call => call.Arguments[3] as Exception)
            .Where(exception => exception is not null)
            .Select(exception => exception!)
            .ToList();

    private const string ExceptionSentinel = "SECRET-EX-SENTINEL";

    /// <summary>A dependency exception whose message and inner message carry a value that must never be logged.</summary>
    private static InvalidOperationException DependencyFailure() =>
        new(
            $"connection string {ExceptionSentinel} outer",
            new InvalidOperationException($"{ExceptionSentinel} inner")
        );

    [TestFixture]
    public class Given_RevokeTokenAsync_WithAValidJtiOwnedByTheCaller : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private Guid _jti;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, _jti.ToString()),
                    new Claim("client_id", OwnerClientId),
                }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_revokes_the_token_by_jti()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
        }

        // Revocation must stay idempotent per RFC 7009, so it must not gate on the stored
        // status the way ValidateTokenAsync does — an already-revoked token is still accepted.
        [Test]
        public void It_does_not_gate_on_the_stored_token_status()
        {
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenOwnedByAnotherClient : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OtherClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenCarryingNoClientId : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // The ownership check is only worth anything if the target token's signature is verified
    // first: otherwise a caller could forge a token naming its own client_id while embedding a
    // victim's jti, and revoke the victim's token. This fixture is exactly that attack.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenForgedToNameTheCaller : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private Guid _victimJti;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, _) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            // Signed with a key the service does not know, but claiming the caller's client_id.
            // The "kid" header names the service's real key so the failure is a genuine
            // signature rejection rather than an unresolved key id.
            var (_, _, attackerKey) = CreateSigningKey();
            attackerKey.KeyId = keyId;
            _victimJti = Guid.NewGuid();
            string forgedToken = CreateSignedToken(
                attackerKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, _victimJti.ToString()),
                    new Claim("client_id", OwnerClientId),
                }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, forgedToken);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_revoke_the_embedded_victim_jti()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithoutAJti : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(signingKey, new[] { new Claim("client_id", OwnerClientId) });

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
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
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, "not-a-valid-guid"),
                    new Claim("client_id", OwnerClientId),
                }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // No credentials is an authentication failure, reported as such before the token is looked
    // at; the endpoint pre-empts this case, so the manager's own guard is defence in depth.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithoutACallerClientId : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), string.Empty, token);
        }

        [Test]
        public void It_reports_invalid_client()
        {
            _result.Should().BeOfType<TokenRevocationResult.InvalidClient>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // An empty client_id claim and a missing one reach the ownership comparison by different
    // routes — FirstOrDefault finds a claim whose value is "" versus finding no claim at all —
    // even though both must end in the same no-op.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenCarryingAnEmptyClientId : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", string.Empty),
                }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // The ownership comparison is deliberately case-sensitive (StringComparison.Ordinal).
    // Switching it to OrdinalIgnoreCase would make the ownership boundary depend on the deployed
    // database engine's collation, so this fixture exists to fail loudly if anyone loosens it.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenWhoseClientIdDiffersOnlyByCase : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", "Owner-Client"),
                }
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), "owner-client", token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // Issuer and audience verification is load-bearing for the ownership boundary, not just
    // signature verification: a token minted by a different issuer (or for a different audience)
    // could carry any client_id it liked. These two fixtures sign with the service's own
    // registered key so that only the issuer/audience claim is wrong.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenFromAnotherIssuer : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                },
                issuer: "https://attacker.example.test"
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenForAnotherAudience : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                },
                audience: "some-other-service"
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // Verification includes the lifetime check, so revoking an already-expired token is a no-op
    // that still reports Completed (RFC 7009 200 OK) while nothing is written. Pinned here so it
    // stays a deliberate decision.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithAnExpiredOwnedToken : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                },
                expired: true
            );

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // An expired token and a forged one both fail verification, but they mean completely
    // different things: the first is routine, the second is the attack signal this change
    // exists to surface. These fixtures assert the observed log levels differ, so the
    // distinction cannot silently regress into a single undifferentiated severity. They also
    // pin D-15: the revocation path logs fixed categories only, never the token, the secret, or
    // the validator's Detail text, and attaches no exception object.
    [TestFixture]
    public class Given_RevokeTokenAsync_LoggingForAnExpiredOwnedToken : OpenIddictTokenManagerTests
    {
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;
        private string _token = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();

            _token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                },
                expired: true
            );

            await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, _token);
        }

        [Test]
        public void It_does_not_log_a_warning()
        {
            LogCountAt(_fakeLogger, LogLevel.Warning).Should().Be(0);
        }

        [Test]
        public void It_logs_at_debug_instead()
        {
            LogCountAt(_fakeLogger, LogLevel.Debug).Should().BeGreaterThan(0);
        }

        [Test]
        public void It_logs_neither_the_token_nor_the_secret()
        {
            AllLoggedText(_fakeLogger).Should().NotContain(_token).And.NotContain(CallerSecret);
        }

        [Test]
        public void It_attaches_no_exception_to_any_log_entry()
        {
            LoggedExceptions(_fakeLogger).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_LoggingForAnUntrustedToken : OpenIddictTokenManagerTests
    {
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;
        private string _token = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, _) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();

            // Signed with a key the service does not hold, but naming its real key in "kid" so
            // the rejection comes from the signature check rather than an unresolved key id.
            var (_, _, attackerKey) = CreateSigningKey();
            attackerKey.KeyId = keyId;

            _token = CreateSignedToken(
                attackerKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                }
            );

            await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, _token);
        }

        [Test]
        public void It_logs_a_warning()
        {
            LogCountAt(_fakeLogger, LogLevel.Warning).Should().BeGreaterThan(0);
        }

        [Test]
        public void It_reports_the_failure_as_signature_related()
        {
            LogMessagesAt(_fakeLogger, LogLevel.Warning)
                .Should()
                .Contain(message => message.Contains("signature or key id"));
        }

        [Test]
        public void It_logs_neither_the_token_nor_the_secret()
        {
            AllLoggedText(_fakeLogger).Should().NotContain(_token).And.NotContain(CallerSecret);
        }

        [Test]
        public void It_attaches_no_exception_to_any_log_entry()
        {
            LoggedExceptions(_fakeLogger).Should().BeEmpty();
        }
    }

    // An Authority or Audience typo fails every token in the environment at once. If that shared
    // the forgery message, the resulting storm would either read as an attack or drown a real one,
    // so issuer/audience rejection is reported as its own category pointing at configuration.
    [TestFixture]
    public class Given_RevokeTokenAsync_LoggingForATokenWithTheWrongAudience : OpenIddictTokenManagerTests
    {
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;
        private string _token = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();

            // Correctly signed by the service's own key; only the audience is unacceptable.
            _token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                },
                audience: "some-other-service"
            );

            await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, _token);
        }

        [Test]
        public void It_logs_a_warning()
        {
            LogCountAt(_fakeLogger, LogLevel.Warning).Should().BeGreaterThan(0);
        }

        [Test]
        public void It_points_at_configuration_rather_than_forgery()
        {
            LogMessagesAt(_fakeLogger, LogLevel.Warning)
                .Should()
                .Contain(message => message.Contains("issuer or audience"));
        }

        [Test]
        public void It_does_not_report_the_failure_as_signature_related()
        {
            LogMessagesAt(_fakeLogger, LogLevel.Warning)
                .Should()
                .NotContain(message => message.Contains("signature or key id"));
        }

        [Test]
        public void It_logs_neither_the_token_nor_the_secret()
        {
            AllLoggedText(_fakeLogger).Should().NotContain(_token).And.NotContain(CallerSecret);
        }

        [Test]
        public void It_attaches_no_exception_to_any_log_entry()
        {
            LoggedExceptions(_fakeLogger).Should().BeEmpty();
        }
    }

    // Where an engine resolves a mis-cased client id (SQL Server does, via its collation), the
    // same registered client can authenticate under different casings on different calls. The
    // claims must not vary with it: they are minted from the stored canonical id, so every token
    // for one client carries one identity.
    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenTheClientUsesNonCanonicalCasing : OpenIddictTokenManagerTests
    {
        private JwtSecurityToken _minted = null!;

        [SetUp]
        public async Task Act()
        {
            StubActiveKeyPair();
            string rawToken = await MintAccessTokenAsAsync(NonCanonicalClientId);
            _minted = new JwtSecurityTokenHandler().ReadJwtToken(rawToken);
        }

        private string? ClaimValue(string type) => _minted.Claims.FirstOrDefault(c => c.Type == type)?.Value;

        [Test]
        public void It_mints_the_canonical_client_id_claim()
        {
            ClaimValue("client_id").Should().Be(CanonicalClientId);
        }

        [Test]
        public void It_mints_the_canonical_sub_claim()
        {
            ClaimValue(JwtRegisteredClaimNames.Sub).Should().Be(CanonicalClientId);
        }

        [Test]
        public void It_mints_the_canonical_azp_claim()
        {
            ClaimValue("azp").Should().Be(CanonicalClientId);
        }
    }

    // The end-to-end proof that the casing defect is fixed. Two tokens for one registered client,
    // obtained under different casings, previously carried different client_id claims, so the
    // holder of one could not revoke the other: the ownership comparison saw two strangers and
    // returned a silent 200 OK no-op. Both tokens now carry the canonical id, so revocation works.
    [TestFixture]
    public class Given_RevokeTokenAsync_AcrossTwoCasingsOfOneClient : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private string _callerClientId = null!;
        private Guid _targetJti;

        [SetUp]
        public async Task Act()
        {
            StubActiveKeyPair();

            // Token to be revoked, obtained using the canonical casing.
            string targetToken = await MintAccessTokenAsAsync(CanonicalClientId);

            // The caller obtained its own token using a different casing of the same client id.
            string callerToken = await MintAccessTokenAsAsync(NonCanonicalClientId);
            _callerClientId = new JwtSecurityTokenHandler()
                .ReadJwtToken(callerToken)
                .Claims.First(c => c.Type == "client_id")
                .Value;

            var targetJti = Guid.Parse(
                new JwtSecurityTokenHandler()
                    .ReadJwtToken(targetToken)
                    .Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti)
                    .Value
            );
            _targetJti = targetJti;
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(targetJti)).Returns(true);

            // The caller authenticates under the non-canonical spelling; the manager must resolve
            // it to the canonical id before comparing it with the target token's claim.
            _result = await CreateMintingTokenManager()
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        NonCanonicalClientId,
                        "plain-secret",
                        targetToken,
                        TokenTypeHint.None
                    ),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_completed()
        {
            _result.Should().BeOfType<TokenRevocationResult.Completed>();
        }

        [Test]
        public void It_revokes_the_token()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_targetJti)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_treats_both_casings_as_the_same_client()
        {
            _callerClientId.Should().Be(CanonicalClientId);
        }
    }

    // DMS-1327 D-07.4 / D-13: every dependency boundary on the revocation path is classified as
    // TemporarilyUnavailable, never as a token outcome, and nothing a dependency exception
    // carries reaches a log. The comparison logic between boundaries is not wrapped (D-13.1).

    private static (string Token, Guid Jti) CreateOwnedToken(RsaSecurityKey signingKey)
    {
        Guid jti = Guid.NewGuid();
        string token = CreateSignedToken(
            signingKey,
            new[]
            {
                new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()),
                new Claim("client_id", OwnerClientId),
            }
        );
        return (token, jti);
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheApplicationLookupThrows : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Throws(DependencyFailure());

            _result = await CreateConfiguredTokenManager(_fakeLogger)
                .RevokeTokenAsync(RevocationRequestFor(OwnerClientId, "any-token"), CancellationToken.None);
        }

        [Test]
        public void It_reports_temporarily_unavailable_at_the_authentication_boundary() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("client-authentication");

        [Test]
        public void It_does_not_load_verification_keys() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync()).MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_logs_an_error_naming_the_exception_types_only()
        {
            LogCountAt(_fakeLogger, LogLevel.Error).Should().Be(1);
            AllLoggedText(_fakeLogger).Should().Contain(typeof(InvalidOperationException).FullName);
        }

        [Test]
        public void It_discloses_nothing_from_the_dependency_exception() =>
            AllLoggedText(_fakeLogger).Should().NotContain(ExceptionSentinel).And.NotContain(CallerSecret);

        [Test]
        public void It_attaches_no_exception_to_any_log_entry() =>
            LoggedExceptions(_fakeLogger).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheSecretHasherThrows : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            StubAuthenticatedCaller(OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(CallerSecret, "hashed-secret"))
                .Throws(DependencyFailure());

            _result = await CreateConfiguredTokenManager(_fakeLogger)
                .RevokeTokenAsync(RevocationRequestFor(OwnerClientId, "any-token"), CancellationToken.None);
        }

        [Test]
        public void It_reports_temporarily_unavailable_at_the_authentication_boundary() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("client-authentication");

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_discloses_nothing_from_the_dependency_exception() =>
            AllLoggedText(_fakeLogger).Should().NotContain(ExceptionSentinel).And.NotContain(CallerSecret);
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheKeyRepositoryThrows : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync()).Throws(DependencyFailure());
            var (_, _, signingKey) = CreateSigningKey();
            var (token, _) = CreateOwnedToken(signingKey);

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, token);
        }

        [Test]
        public void It_reports_temporarily_unavailable_at_the_key_retrieval_boundary() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("signing-key-retrieval");

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_discloses_nothing_from_the_dependency_exception() =>
            AllLoggedText(_fakeLogger).Should().NotContain(ExceptionSentinel);

        [Test]
        public void It_attaches_no_exception_to_any_log_entry() =>
            LoggedExceptions(_fakeLogger).Should().BeEmpty();
    }

    // The old GetPublicKeysFromDatabaseAsync path skipped a corrupt key and carried on, so a
    // corrupt active key turned every revocation into an "unknown token" 200. The revocation
    // loader treats it as unavailable instead.
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenAnActivePublicKeyIsCorrupt : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;
        private string _corruptKeyId = null!;

        [SetUp]
        public async Task Act()
        {
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            _corruptKeyId = Guid.NewGuid().ToString();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                        new PublicKeyInfo { KeyId = _corruptKeyId, PublicKey = [1, 2, 3, 4] },
                    }
                );
            var (token, _) = CreateOwnedToken(signingKey);

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, token);
        }

        [Test]
        public void It_reports_temporarily_unavailable_at_the_key_import_boundary() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("signing-key-import");

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_logs_an_error_naming_the_corrupt_key()
        {
            LogCountAt(_fakeLogger, LogLevel.Error).Should().Be(1);
            LogMessagesAt(_fakeLogger, LogLevel.Error)
                .Should()
                .ContainSingle(message => message.Contains(_corruptKeyId));
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenNoActivePublicKeyExists : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync()).Returns(Array.Empty<PublicKeyInfo>());
            var (_, _, signingKey) = CreateSigningKey();
            var (token, _) = CreateOwnedToken(signingKey);

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, token);
        }

        [Test]
        public void It_reports_temporarily_unavailable_for_the_empty_key_set() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("no-active-signing-keys");

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_logs_an_error() => LogCountAt(_fakeLogger, LogLevel.Error).Should().Be(1);
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheCertificatePathIsMisconfigured : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var manager = new OpenIddictTokenManager(
                Options.Create(
                    new IdentityOptions
                    {
                        Authority = TestIssuer,
                        Audience = TestAudience,
                        UseCertificates = true,
                        UseDevelopmentCertificates = false,
                        CertificatePath = string.Empty,
                    }
                ),
                NullLogger<OpenIddictTokenManager>.Instance,
                _secretHasher,
                _tokenRepository
            );
            var (_, _, signingKey) = CreateSigningKey();
            var (token, _) = CreateOwnedToken(signingKey);

            _result = await RevokeAsAsync(manager, OwnerClientId, token);
        }

        [Test]
        public void It_reports_temporarily_unavailable_at_the_key_retrieval_boundary() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("signing-key-retrieval");

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
    }

    // A token whose kid names no loaded key is an ordinary untrusted token against a healthy key
    // set: a token outcome (Completed), not an outage. The kid is attacker-chosen, so the log must
    // not echo it.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithAnUnknownKid : OpenIddictTokenManagerTests
    {
        private const string KidSentinel = "SECRET-KID-SENTINEL";
        private TokenRevocationResult _result = null!;
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            var (keyId, publicKeySpki, _) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            var (_, _, strangerKey) = CreateSigningKey();
            strangerKey.KeyId = KidSentinel;
            var (token, _) = CreateOwnedToken(strangerKey);

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed() => _result.Should().BeOfType<TokenRevocationResult.Completed>();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_logs_a_warning_in_the_signature_category() =>
            LogMessagesAt(_fakeLogger, LogLevel.Warning)
                .Should()
                .Contain(message => message.Contains("signature or key id"));

        [Test]
        public void It_does_not_log_the_unverified_kid() =>
            AllLoggedText(_fakeLogger).Should().NotContain(KidSentinel);
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheUpdateThrows : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            var (token, jti) = CreateOwnedToken(signingKey);
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(jti)).Throws(DependencyFailure());

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(_fakeLogger), OwnerClientId, token);
        }

        [Test]
        public void It_reports_temporarily_unavailable_at_the_mutation_boundary() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("token-mutation");

        [Test]
        public void It_discloses_nothing_from_the_dependency_exception() =>
            AllLoggedText(_fakeLogger).Should().NotContain(ExceptionSentinel).And.NotContain(CallerSecret);

        [Test]
        public void It_attaches_no_exception_to_any_log_entry() =>
            LoggedExceptions(_fakeLogger).Should().BeEmpty();
    }

    // The UPDATE reached the database and then the connection died before the row count came
    // back. The manager cannot know whether the row changed, so it reports unavailable and says
    // nothing about the token's state (D-13.3). Deliberately, no assertion here claims the token
    // is revoked or still live.
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheResponseIsLostAfterTheUpdate : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private bool _updateReachedTheStore;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            var (token, jti) = CreateOwnedToken(signingKey);
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(jti))
                .Invokes(() => _updateReachedTheStore = true)
                .Throws(new TimeoutException("the connection dropped while reading the result"));

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_had_already_issued_the_update() => _updateReachedTheStore.Should().BeTrue();

        [Test]
        public void It_reports_temporarily_unavailable_rather_than_a_token_outcome() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("token-mutation");
    }

    // Authentication precedes any token evaluation: with bad credentials the token is never
    // decoded, no key is loaded and the store is never touched, so an unknown or malformed token
    // cannot turn an authentication failure into a 200.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithBadCredentialsAndAnUnknownToken : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Returns((ApplicationInfo?)null);

            _result = await CreateConfiguredTokenManager()
                .RevokeTokenAsync(
                    RevocationRequestFor(OwnerClientId, "not-even-a-jwt"),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_invalid_client() =>
            _result.Should().BeOfType<TokenRevocationResult.InvalidClient>();

        [Test]
        public void It_does_not_load_verification_keys() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync()).MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
    }

    // The caller going away is not an outage: the cancellation propagates so no response is
    // written, instead of being relabelled as a 503.
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheCallerCancels : OpenIddictTokenManagerTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Arrange()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            CancellationToken token = cancellation.Token;
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Throws(new OperationCanceledException(token));

            _act = () =>
                CreateConfiguredTokenManager()
                    .RevokeTokenAsync(RevocationRequestFor(OwnerClientId, "any-token"), token);
        }

        [Test]
        public async Task It_propagates_the_cancellation() =>
            await _act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Injects a fault into the ownership comparison, which sits between the dependency
    /// boundaries and must therefore not be wrapped.
    /// </summary>
    private sealed class FaultingOwnershipTokenManager(
        IOptions<IdentityOptions> identityOptions,
        IClientSecretHasher secretHasher,
        IOpenIddictTokenRepository tokenRepository
    )
        : OpenIddictTokenManager(
            identityOptions,
            NullLogger<OpenIddictTokenManager>.Instance,
            secretHasher,
            tokenRepository
        )
    {
        protected override bool TokenBelongsToCaller(string? tokenClientId, string callerClientId) =>
            throw new InvalidOperationException("ownership comparison fault");
    }

    // A bug in the comparison logic is a programming fault, not an outage: it must surface as an
    // exception (a 500 at the HTTP level), never as a 503 that would relabel it, and never as a
    // 200 (D-13.1, Q-04).
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheOwnershipComparisonFaults : OpenIddictTokenManagerTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Arrange()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            StubAuthenticatedCaller(OwnerClientId);
            var (token, _) = CreateOwnedToken(signingKey);
            var manager = new FaultingOwnershipTokenManager(
                Options.Create(new IdentityOptions { Authority = TestIssuer, Audience = TestAudience }),
                _secretHasher,
                _tokenRepository
            );

            _act = () =>
                manager.RevokeTokenAsync(RevocationRequestFor(OwnerClientId, token), CancellationToken.None);
        }

        [Test]
        public async Task It_lets_the_fault_propagate() =>
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("ownership comparison fault");

        [Test]
        public async Task It_does_not_touch_the_token_store()
        {
            await _act.Should().ThrowAsync<InvalidOperationException>();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    /// <summary>
    /// A token manager whose only non-default setting is the per-client token limit, so a test
    /// asserting on that number is asserting on a configured value rather than on the default.
    /// </summary>
    private OpenIddictTokenManager CreateTokenManagerWithTokenLimit(int limit) =>
        new(
            // EncryptionKey has to be set for the database signing-key path to run at all; the
            // faked repository ignores its value.
            Options.Create(
                new IdentityOptions
                {
                    Authority = TestIssuer,
                    Audience = TestAudience,
                    EncryptionKey = "test-encryption-key",
                    BearerTokenPerClientLimit = limit,
                }
            ),
            NullLogger<OpenIddictTokenManager>.Instance,
            _secretHasher,
            _tokenRepository
        );

    /// <summary>
    /// Arranges an approved client with a matching secret and a usable signing key, which is what
    /// GetAccessTokenAsync needs before it reaches StoreTokenAsync.
    /// </summary>
    private Guid ArrangeGrantableClient()
    {
        Guid applicationId = Guid.NewGuid();

        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(GrantClientId))
            .Returns(
                new ApplicationInfo
                {
                    Id = applicationId,
                    ClientId = GrantClientId,
                    ClientSecret = "hashed-secret",
                    IsApproved = true,
                    ProtocolMappers = "[]",
                }
            );

        A.CallTo(() => _secretHasher.VerifySecretAsync(GrantClientSecret, "hashed-secret")).Returns(true);

        using RSA rsa = RSA.Create(2048);
        A.CallTo(() => _tokenRepository.GetActivePrivateKeyAsync(A<string>._))
            .Returns(
                new PrivateKeyInfo
                {
                    PrivateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
                    KeyId = Guid.NewGuid().ToString(),
                }
            );

        A.CallTo(() => _tokenRepository.GetClientRolesAsync(applicationId)).Returns([]);

        return applicationId;
    }

    private static List<KeyValuePair<string, string>> GrantCredentials() =>
        [new("client_id", GrantClientId), new("client_secret", GrantClientSecret)];

    /// <summary>
    /// Makes the faked repository answer every store attempt with the given outcome, and records
    /// the arguments it was handed.
    /// </summary>
    private void ArrangeStoreOutcome(TokenStoreOutcome outcome, Action<StoredTokenCall> capture)
    {
        A.CallTo(() =>
                _tokenRepository.StoreTokenAsync(
                    A<Guid>._,
                    A<Guid>._,
                    A<string>._,
                    A<DateTimeOffset>._,
                    A<int>._
                )
            )
            .Invokes(
                (
                    Guid tokenId,
                    Guid applicationId,
                    string subject,
                    DateTimeOffset expiration,
                    int maxActiveTokens
                ) =>
                    capture(new StoredTokenCall(tokenId, applicationId, subject, expiration, maxActiveTokens))
            )
            .Returns(outcome);
    }

    private sealed record StoredTokenCall(
        Guid TokenId,
        Guid ApplicationId,
        string Subject,
        DateTimeOffset Expiration,
        int MaxActiveTokens
    );

    private const string GrantClientId = "grant-client";
    private const string GrantClientSecret = "plain-secret";

    // The limit reaching the repository is the whole of the enforcement wiring, and nothing else
    // in this file asserts the StoreTokenAsync call at all - so without this fixture, dropping the
    // argument or passing the wrong one would break the feature without failing a test.
    [TestFixture]
    public class Given_GetAccessTokenAsync_StoresATokenForAnApprovedClient : OpenIddictTokenManagerTests
    {
        private const int ConfiguredLimit = 3;

        private StoredTokenCall _call = null!;
        private Guid _applicationId;
        private DateTimeOffset _before;
        private DateTimeOffset _after;
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            _applicationId = ArrangeGrantableClient();
            ArrangeStoreOutcome(TokenStoreOutcome.Stored, call => _call = call);

            _before = DateTimeOffset.UtcNow;
            _result = await CreateTokenManagerWithTokenLimit(ConfiguredLimit)
                .GetAccessTokenAsync(GrantCredentials());
            _after = DateTimeOffset.UtcNow;
        }

        [Test]
        public void It_passes_the_configured_limit() => _call.MaxActiveTokens.Should().Be(ConfiguredLimit);

        [Test]
        public void It_passes_the_application_id() => _call.ApplicationId.Should().Be(_applicationId);

        [Test]
        public void It_passes_the_client_id_as_the_subject() => _call.Subject.Should().Be(GrantClientId);

        private static string AccessTokenFrom(TokenResult result)
        {
            string json = result.Should().BeOfType<TokenResult.Success>().Subject.Token;
            return System
                .Text.Json.JsonDocument.Parse(json)
                .RootElement.GetProperty("access_token")
                .GetString()!;
        }

        [Test]
        public void It_passes_the_token_id_that_was_minted_as_the_jti()
        {
            string accessToken = AccessTokenFrom(_result);
            string jti = new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Id;
            jti.Should().Be(_call.TokenId.ToString());
        }

        [Test]
        public void It_passes_an_expiration_one_token_lifetime_ahead()
        {
            // The manager reads its own UtcNow, so the assertion brackets the call rather than
            // pinning an instant. 30 minutes is the IdentityOptions default this manager was
            // built with.
            _call.Expiration.Should().BeOnOrAfter(_before.AddMinutes(30));
            _call.Expiration.Should().BeOnOrBefore(_after.AddMinutes(30));
        }

        [Test]
        public void It_returns_a_success_result() => _result.Should().BeOfType<TokenResult.Success>();
    }

    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenTheClientIsAtItsTokenLimit : OpenIddictTokenManagerTests
    {
        private const int ConfiguredLimit = 3;

        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            ArrangeGrantableClient();
            ArrangeStoreOutcome(TokenStoreOutcome.LimitExceeded, _ => { });

            _result = await CreateTokenManagerWithTokenLimit(ConfiguredLimit)
                .GetAccessTokenAsync(GrantCredentials());
        }

        [Test]
        public void It_returns_a_token_limit_failure_carrying_the_configured_limit() =>
            _result.Should().BeEquivalentTo(new TokenResult.FailureTokenLimitExceeded(ConfiguredLimit));

        [Test]
        public void It_does_not_return_a_token() => _result.Should().NotBeOfType<TokenResult.Success>();
    }

    // A client deleted between the lookup and the store must get the unknown-client answer, never
    // "Too Many Tokens" - the three-state outcome exists precisely so these two cannot be confused.
    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenTheClientVanishedBeforeTheStore : OpenIddictTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            ArrangeGrantableClient();
            ArrangeStoreOutcome(TokenStoreOutcome.ClientNotFound, _ => { });

            _result = await CreateTokenManagerWithTokenLimit(3).GetAccessTokenAsync(GrantCredentials());
        }

        [Test]
        public void It_returns_the_same_invalid_client_failure_an_unknown_client_receives() =>
            _result
                .Should()
                .BeEquivalentTo(
                    new TokenResult.FailureAuthentication(
                        "invalid_client",
                        "Invalid client or Invalid client credentials"
                    )
                );

        [Test]
        public void It_is_not_reported_as_a_token_limit_failure() =>
            _result.Should().NotBeOfType<TokenResult.FailureTokenLimitExceeded>();
    }

    // The arm has to sit ahead of the catch-all: without it the repository's contention outcome
    // would fall through to FailureUnknown and answer a transient queue with a server error.
    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenTheGrantCouldNotBeSerialized : OpenIddictTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            ArrangeGrantableClient();
            ArrangeStoreOutcome(TokenStoreOutcome.LockTimeout, _ => { });

            _result = await CreateTokenManagerWithTokenLimit(3).GetAccessTokenAsync(GrantCredentials());
        }

        [Test]
        public void It_returns_a_lock_timeout_failure() =>
            _result.Should().BeOfType<TokenResult.FailureLockTimeout>();
    }

    // Disabling is the repository's job, not the manager's: the manager forwards whatever is
    // configured, so a manager-side shortcut cannot quietly diverge from the disable semantics.
    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenTheConfiguredLimitDisablesEnforcement
        : OpenIddictTokenManagerTests
    {
        private StoredTokenCall _call = null!;
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            ArrangeGrantableClient();
            ArrangeStoreOutcome(TokenStoreOutcome.Stored, call => _call = call);

            _result = await CreateTokenManagerWithTokenLimit(-1).GetAccessTokenAsync(GrantCredentials());
        }

        [Test]
        public void It_forwards_the_disabling_value_verbatim() => _call.MaxActiveTokens.Should().Be(-1);

        [Test]
        public void It_returns_a_success_result() => _result.Should().BeOfType<TokenResult.Success>();
    }
}
