// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using TokenValidationResult = EdFi.DmsConfigurationService.Backend.OpenIddict.Validation.TokenValidationResult;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class OpenIddictTokenManagerTests
{
    private IClientSecretHasher _secretHasher = null!;
    private IOpenIddictTokenRepository _tokenRepository = null!;
    private SigningKeySnapshotProvider _signingKeyProvider = null!;
    private DevelopmentCertificateStore _developmentCertificateStore = null!;
    private OpenIddictTokenManager _tokenManager = null!;

    [SetUp]
    public void Setup()
    {
        _secretHasher = A.Fake<IClientSecretHasher>();
        _tokenRepository = A.Fake<IOpenIddictTokenRepository>();

        // The real provider over the faked repository, so key reads go through the same snapshot, source and parser
        // as in production. Created per test: NUnit reuses fixture instances.
        _signingKeyProvider = new SigningKeySnapshotProvider(
            new DatabaseSigningKeySource(_tokenRepository, NullLogger<DatabaseSigningKeySource>.Instance),
            Options.Create(new IdentityOptions()),
            TimeProvider.System,
            NullLogger<SigningKeySnapshotProvider>.Instance
        );
        _developmentCertificateStore = new DevelopmentCertificateStore(
            Options.Create(new IdentityOptions()),
            NullLogger<DevelopmentCertificateStore>.Instance
        );

        _tokenManager = NewTokenManager(new IdentityOptions());
    }

    [TearDown]
    public void DisposeSigningKeyServices()
    {
        _signingKeyProvider.Dispose();
        _developmentCertificateStore.Dispose();
    }

    private const string TestIssuer = "https://cms.example.test";
    private const string TestAudience = "ed-fi-cms-tests";

    /// <summary>
    /// A token manager over the fixture's faked repository and secret hasher. Keys come from
    /// <paramref name="signingKeyProvider"/>, or from the fixture's real provider over the faked repository.
    /// </summary>
    private OpenIddictTokenManager NewTokenManager(
        IdentityOptions options,
        ILogger<OpenIddictTokenManager>? logger = null,
        ISigningKeySnapshotProvider? signingKeyProvider = null,
        DevelopmentCertificateStore? developmentCertificateStore = null
    ) =>
        new(
            Options.Create(options),
            logger ?? NullLogger<OpenIddictTokenManager>.Instance,
            _secretHasher,
            _tokenRepository,
            signingKeyProvider ?? _signingKeyProvider,
            developmentCertificateStore ?? _developmentCertificateStore
        );

    /// <summary>
    /// Builds a token manager configured with the test issuer/audience so that
    /// ValidateTokenAsync can verify tokens produced by the helpers below.
    /// </summary>
    private OpenIddictTokenManager CreateConfiguredTokenManager(
        ILogger<OpenIddictTokenManager>? logger = null,
        ISigningKeySnapshotProvider? signingKeyProvider = null
    ) =>
        NewTokenManager(
            new IdentityOptions { Authority = TestIssuer, Audience = TestAudience },
            logger,
            signingKeyProvider
        );

    /// <summary>
    /// Counts entries the manager wrote at a given level. The logging extension methods funnel
    /// into <see cref="ILogger.Log{TState}"/>, whose first argument is the level, so intercepting
    /// that one method observes every call regardless of which extension produced it.
    /// </summary>
    private static int LogCountAt<T>(ILogger<T> logger, LogLevel level) =>
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
    /// that the faked repository returns from GetActivePublicKeysAsync(CancellationToken).
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
        private readonly Guid _applicationId = Guid.NewGuid();

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
                        Id = _applicationId,
                        ClientId = "known-client",
                        ClientSecret = "hashed-secret",
                        IsApproved = true,
                    }
                );
            A.CallTo(() => _secretHasher.VerifySecretAsync("plain-secret", "hashed-secret")).Returns(true);

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();

        [Test]
        public void It_constrains_the_update_to_the_application_the_lookup_resolved() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, _applicationId))
                .MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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

        A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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
        NewTokenManager(
            new IdentityOptions
            {
                Authority = TestIssuer,
                Audience = TestAudience,
                EncryptionKey = TestEncryptionKey,
            }
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
        A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
            .Returns(
                new[]
                {
                    new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                }
            );

    private const string CallerSecret = "plain-secret";

    /// <summary>The stored application Id <see cref="StubAuthenticatedCaller"/> registered last.</summary>
    private Guid _callerApplicationId;

    /// <summary>
    /// Registers <paramref name="clientId"/> as an approved application whose secret the faked
    /// hasher accepts, so a revocation request carrying <see cref="CallerSecret"/> authenticates.
    /// </summary>
    private void StubAuthenticatedCaller(string clientId)
    {
        _callerApplicationId = Guid.NewGuid();
        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(clientId))
            .Returns(
                new ApplicationInfo
                {
                    Id = _callerApplicationId,
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
    private static string AllLoggedText<T>(ILogger<T> logger)
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
    private static IReadOnlyList<Exception> LoggedExceptions<T>(ILogger<T> logger) =>
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();
        }

        // The UPDATE is constrained by the token's ApplicationId foreign key (DMS-1327 D-08), so
        // the authenticated application's stored Id, not some other value, must reach it.
        [Test]
        public void It_constrains_the_update_to_the_authenticated_application()
        {
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, _callerApplicationId))
                .MustHaveHappenedOnceExactly();
        }

        // Revocation must stay idempotent per RFC 7009, so it must not gate on the stored
        // status the way ValidateTokenAsync does — an already-revoked token is still accepted.
        [Test]
        public void It_does_not_gate_on_the_stored_token_status()
        {
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
        }
    }

    // Zero rows changed covers an unknown token, an already revoked one, and a row stored for a
    // different application; RFC 7009 answers all of them the same way (DMS-1327 D-07 step 8).
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheConstrainedUpdateChangesNoRow : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private Guid _jti;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            (string token, _jti) = CreateOwnedToken(signingKey);
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).Returns(false);

            _result = await RevokeAsAsync(CreateConfiguredTokenManager(), OwnerClientId, token);
        }

        [Test]
        public void It_reports_completed() => _result.Should().BeOfType<TokenRevocationResult.Completed>();

        [Test]
        public void It_attempted_the_update_for_the_authenticated_application() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, _callerApplicationId))
                .MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(targetJti, A<Guid>._)).Returns(true);

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_targetJti, A<Guid>._))
                .MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .Throws(DependencyFailure());
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .Returns(Array.Empty<PublicKeyInfo>());
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

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
            var manager = NewTokenManager(
                new IdentityOptions
                {
                    Authority = TestIssuer,
                    Audience = TestAudience,
                    UseCertificates = true,
                    UseDevelopmentCertificates = false,
                    CertificatePath = string.Empty,
                }
            );
            var (_, _, signingKey) = CreateSigningKey();
            var (token, _) = CreateOwnedToken(signingKey);

            _result = await RevokeAsAsync(manager, OwnerClientId, token);
        }

        [Test]
        public void It_reports_temporarily_unavailable_for_the_missing_certificate() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("signing-certificate-missing");

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(jti, A<Guid>._)).Throws(DependencyFailure());

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(jti, A<Guid>._))
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
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
        IOpenIddictTokenRepository tokenRepository,
        ISigningKeySnapshotProvider signingKeyProvider,
        DevelopmentCertificateStore developmentCertificateStore
    )
        : OpenIddictTokenManager(
            identityOptions,
            NullLogger<OpenIddictTokenManager>.Instance,
            secretHasher,
            tokenRepository,
            signingKeyProvider,
            developmentCertificateStore
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
                _tokenRepository,
                _signingKeyProvider,
                _developmentCertificateStore
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
        }
    }

    // DMS-1327 P2.1 correction. Catching around a dependency is not enough when the dependency
    // suppresses its own failures (the registered ClientSecretHasher answers false for anything that
    // goes wrong) or repairs missing state (the JWKS certificate helper creates a development
    // certificate when the file is gone). These fixtures run the real hasher and real certificate
    // files rather than fakes, because a throwing fake cannot reveal either behaviour.

    private const int HashedAtIterations = 1000;
    private const string RealHasherSecret = "SECRET-CALLER-SENTINEL-real-hasher";

    private static ClientSecretHasher CreateRealHasher(
        int iterations,
        ILogger<ClientSecretHasher>? logger = null
    ) =>
        new(
            logger ?? NullLogger<ClientSecretHasher>.Instance,
            Options.Create(new IdentityOptions { ClientSecretHashingIterations = iterations })
        );

    private static async Task<string> HashedAtValidIterations(string secret) =>
        await CreateRealHasher(HashedAtIterations).HashSecretAsync(secret);

    private void StubApplicationWithStoredSecret(string clientId, string storedSecret) =>
        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(clientId))
            .Returns(
                new ApplicationInfo
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    ClientSecret = storedSecret,
                    IsApproved = true,
                    Permissions = ["edfi_admin_api/full_access"],
                }
            );

    private OpenIddictTokenManager CreateManagerWith(
        IClientSecretHasher hasher,
        ILogger<OpenIddictTokenManager>? logger = null,
        IdentityOptions? options = null
    ) =>
        new(
            Options.Create(
                options ?? new IdentityOptions { Authority = TestIssuer, Audience = TestAudience }
            ),
            logger ?? NullLogger<OpenIddictTokenManager>.Instance,
            hasher,
            _tokenRepository,
            _signingKeyProvider,
            _developmentCertificateStore
        );

    // A deterministic configuration failure inside the real hasher: verification cannot run at all.
    // Before the correction this was answered InvalidClient (401) and the hasher logged the
    // exception and its message. Startup validation rejects a zero count in production, so this is
    // a stand-in for any failure the hasher would otherwise turn into "wrong secret".
    [TestFixture]
    public class Given_RevokeTokenAsync_WithTheRealHasherWhenVerificationCannotRun
        : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<ClientSecretHasher> _hasherLogger = null!;
        private ILogger<OpenIddictTokenManager> _managerLogger = null!;
        private string _dependencyMessage = null!;

        /// <summary>The message the hasher's own dependency throws for a zero iteration count, captured so a test can assert it never reaches a log.</summary>
        private static string ZeroIterationFailureMessage()
        {
            try
            {
                Rfc2898DeriveBytes.Pbkdf2("x", new byte[16], 0, HashAlgorithmName.SHA256, 32);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return ex.Message;
            }

            throw new InvalidOperationException("PBKDF2 accepted a zero iteration count.");
        }

        [SetUp]
        public async Task Act()
        {
            _dependencyMessage = ZeroIterationFailureMessage();
            _hasherLogger = A.Fake<ILogger<ClientSecretHasher>>();
            _managerLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            StubApplicationWithStoredSecret(OwnerClientId, await HashedAtValidIterations(RealHasherSecret));

            _result = await CreateManagerWith(CreateRealHasher(0, _hasherLogger), _managerLogger)
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        OwnerClientId,
                        RealHasherSecret,
                        "any-token",
                        TokenTypeHint.None
                    ),
                    CancellationToken.None
                );
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_names_the_dependency_exception_type_in_the_manager_log() =>
            LogMessagesAt(_managerLogger, LogLevel.Error)
                .Should()
                .ContainSingle(message => message.Contains(typeof(ArgumentOutOfRangeException).FullName!));

        [Test]
        public void It_keeps_the_dependency_message_out_of_every_log()
        {
            AllLoggedText(_managerLogger).Should().NotContain(_dependencyMessage);
            AllLoggedText(_hasherLogger).Should().NotContain(_dependencyMessage);
        }

        [Test]
        public void It_keeps_the_presented_secret_out_of_every_log()
        {
            AllLoggedText(_managerLogger).Should().NotContain(RealHasherSecret);
            AllLoggedText(_hasherLogger).Should().NotContain(RealHasherSecret);
        }

        [Test]
        public void It_attaches_no_exception_to_any_log_entry()
        {
            LoggedExceptions(_managerLogger).Should().BeEmpty();
            LoggedExceptions(_hasherLogger).Should().BeEmpty();
        }

        [Test]
        public void It_logs_no_hasher_warning() => LogCountAt(_hasherLogger, LogLevel.Warning).Should().Be(0);
    }

    // Stored-data corruption: the hash cannot be decoded, so the secret was never compared. That is
    // not evidence the caller's credentials are wrong.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithTheRealHasherAndAnUnreadableStoredHash
        : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            // Valid base64 of three bytes: the reader runs out before the salt length.
            StubApplicationWithStoredSecret(OwnerClientId, Convert.ToBase64String([1, 2, 3]));

            _result = await CreateManagerWith(CreateRealHasher(HashedAtIterations))
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        OwnerClientId,
                        RealHasherSecret,
                        "any-token",
                        TokenTypeHint.None
                    ),
                    CancellationToken.None
                );
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    // A real generated hash missing its last decoded byte: the salt is intact and the stored subkey
    // is 31 bytes, so without the structural check the comparison quietly answers "wrong secret".
    [TestFixture]
    public class Given_RevokeTokenAsync_WithTheRealHasherAndATruncatedRealHash : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ILogger<ClientSecretHasher> _hasherLogger = null!;
        private ILogger<OpenIddictTokenManager> _managerLogger = null!;
        private string _truncatedHash = null!;

        [SetUp]
        public async Task Act()
        {
            _hasherLogger = A.Fake<ILogger<ClientSecretHasher>>();
            _managerLogger = A.Fake<ILogger<OpenIddictTokenManager>>();
            byte[] decoded = Convert.FromBase64String(await HashedAtValidIterations(RealHasherSecret));
            _truncatedHash = Convert.ToBase64String(decoded[..^1]);
            StubApplicationWithStoredSecret(OwnerClientId, _truncatedHash);

            _result = await CreateManagerWith(
                    CreateRealHasher(HashedAtIterations, _hasherLogger),
                    _managerLogger
                )
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        OwnerClientId,
                        RealHasherSecret,
                        "any-token",
                        TokenTypeHint.None
                    ),
                    CancellationToken.None
                );
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_names_only_the_exception_type_in_the_manager_log() =>
            LogMessagesAt(_managerLogger, LogLevel.Error)
                .Should()
                .ContainSingle(message => message.Contains(typeof(InvalidDataException).FullName!));

        [Test]
        public void It_keeps_the_secret_and_the_stored_hash_out_of_every_log()
        {
            AllLoggedText(_managerLogger)
                .Should()
                .NotContain(RealHasherSecret)
                .And.NotContain(_truncatedHash);
            AllLoggedText(_hasherLogger).Should().NotContain(RealHasherSecret).And.NotContain(_truncatedHash);
        }

        [Test]
        public void It_attaches_no_exception_to_any_log_entry()
        {
            LoggedExceptions(_managerLogger).Should().BeEmpty();
            LoggedExceptions(_hasherLogger).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithTheRealHasherAndAWrongSecret : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            StubApplicationWithStoredSecret(OwnerClientId, await HashedAtValidIterations(RealHasherSecret));

            _result = await CreateManagerWith(CreateRealHasher(HashedAtIterations))
                .RevokeTokenAsync(
                    new TokenRevocationRequest(
                        OwnerClientId,
                        "not-the-secret",
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_WithTheRealHasherAndTheRightSecret : OpenIddictTokenManagerTests
    {
        private TokenRevocationResult _result = null!;
        private Guid _jti;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            StubApplicationWithStoredSecret(OwnerClientId, await HashedAtValidIterations(RealHasherSecret));
            (string token, _jti) = CreateOwnedToken(signingKey);
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);

            _result = await CreateManagerWith(CreateRealHasher(HashedAtIterations))
                .RevokeTokenAsync(
                    new TokenRevocationRequest(OwnerClientId, RealHasherSecret, token, TokenTypeHint.None),
                    CancellationToken.None
                );
        }

        [Test]
        public void It_reports_completed() => _result.Should().BeOfType<TokenRevocationResult.Completed>();

        [Test]
        public void It_revokes_the_token() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    // The token endpoint is out of scope and keeps the hasher's established semantics: a
    // verification that cannot run is still reported as unauthorized_client, and the hasher still
    // logs it. Pinned so the revocation fix cannot silently change /connect/token.
    [TestFixture]
    public class Given_GetAccessTokenAsync_WithTheRealHasherWhenVerificationCannotRun
        : OpenIddictTokenManagerTests
    {
        private TokenResult _result = null!;
        private ILogger<ClientSecretHasher> _hasherLogger = null!;

        [SetUp]
        public async Task Act()
        {
            _hasherLogger = A.Fake<ILogger<ClientSecretHasher>>();
            StubApplicationWithStoredSecret(OwnerClientId, await HashedAtValidIterations(RealHasherSecret));

            _result = await CreateManagerWith(CreateRealHasher(0, _hasherLogger))
                .GetAccessTokenAsync([
                    new KeyValuePair<string, string>("client_id", OwnerClientId),
                    new KeyValuePair<string, string>("client_secret", RealHasherSecret),
                ]);
        }

        [Test]
        public void It_still_answers_unauthorized_client() =>
            _result
                .Should()
                .BeOfType<TokenResult.FailureAuthentication>()
                .Which.Error.Should()
                .Be("unauthorized_client");

        [Test]
        public void It_still_has_the_hasher_log_the_failure() =>
            LogCountAt(_hasherLogger, LogLevel.Warning).Should().Be(1);
    }

    /// <summary>A scratch directory for certificate files, removed by the fixture's teardown.</summary>
    private static string NewScratchDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dms-1327-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private const string DevCertificatePassword = "test-password";

    /// <summary>
    /// Writes a password-protected development certificate to <paramref name="path"/> and returns a
    /// signing key for its private half whose key id is the certificate thumbprint, which is what
    /// the service puts in "kid" when it signs with a certificate.
    /// </summary>
    private static RsaSecurityKey WriteDevelopmentCertificate(string path)
    {
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=DevCert",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1)
        );
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, DevCertificatePassword));
        return new RsaSecurityKey(rsa) { KeyId = certificate.Thumbprint };
    }

    private static IdentityOptions DevelopmentCertificateOptions(string path) =>
        new()
        {
            Authority = TestIssuer,
            Audience = TestAudience,
            UseCertificates = true,
            UseDevelopmentCertificates = true,
            DevCertificatePath = path,
            DevCertificatePassword = DevCertificatePassword,
        };

    // The JWKS helper would create a replacement development certificate here. A replacement holds
    // an unrelated key, the owner's token would fail verification against it, and revocation would
    // answer an empty 200 while revoking nothing. Revocation must refuse instead and leave the
    // filesystem alone.
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheDevelopmentCertificateIsMissing : OpenIddictTokenManagerTests
    {
        private string _directory = null!;
        private string _certificatePath = null!;
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            _directory = NewScratchDirectory();
            _certificatePath = Path.Combine(_directory, "devcert.pfx");

            // The owner's token was signed by the certificate that has since gone missing.
            RsaSecurityKey originalKey = WriteDevelopmentCertificate(_certificatePath);
            File.Delete(_certificatePath);
            var (token, _) = CreateOwnedToken(originalKey);

            var manager = CreateManagerWith(
                _secretHasher,
                options: DevelopmentCertificateOptions(_certificatePath)
            );
            _result = await RevokeAsAsync(manager, OwnerClientId, token);
        }

        [TearDown]
        public void RemoveScratchDirectory() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void It_reports_temporarily_unavailable_for_the_missing_certificate() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("signing-certificate-missing");

        [Test]
        public void It_does_not_create_a_replacement_certificate() =>
            File.Exists(_certificatePath).Should().BeFalse();

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    // The read-only loader still verifies against a certificate that exists: the owner's token is
    // revoked and the certificate file is left byte-for-byte unchanged.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithAnExistingDevelopmentCertificate : OpenIddictTokenManagerTests
    {
        private string _directory = null!;
        private string _certificatePath = null!;
        private byte[] _certificateBytes = null!;
        private TokenRevocationResult _result = null!;
        private Guid _jti;

        [SetUp]
        public async Task Act()
        {
            _directory = NewScratchDirectory();
            _certificatePath = Path.Combine(_directory, "devcert.pfx");
            RsaSecurityKey signingKey = WriteDevelopmentCertificate(_certificatePath);
            _certificateBytes = await File.ReadAllBytesAsync(_certificatePath);
            (string token, _jti) = CreateOwnedToken(signingKey);
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);

            var manager = CreateManagerWith(
                _secretHasher,
                options: DevelopmentCertificateOptions(_certificatePath)
            );
            _result = await RevokeAsAsync(manager, OwnerClientId, token);
        }

        [TearDown]
        public void RemoveScratchDirectory() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void It_reports_completed() => _result.Should().BeOfType<TokenRevocationResult.Completed>();

        [Test]
        public void It_revokes_the_token() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();

        [Test]
        public void It_leaves_the_certificate_file_unchanged() =>
            File.ReadAllBytes(_certificatePath).Should().Equal(_certificateBytes);
    }

    // A certificate that exists but cannot be read is an operational failure at the key-retrieval
    // boundary, not an untrusted token.
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenTheProductionCertificateIsUnreadable : OpenIddictTokenManagerTests
    {
        private string _directory = null!;
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            _directory = NewScratchDirectory();
            string certificatePath = Path.Combine(_directory, "signing.pfx");
            await File.WriteAllBytesAsync(certificatePath, [1, 2, 3, 4, 5]);
            var (_, _, signingKey) = CreateSigningKey();
            var (token, _) = CreateOwnedToken(signingKey);

            var manager = CreateManagerWith(
                _secretHasher,
                options: new IdentityOptions
                {
                    Authority = TestIssuer,
                    Audience = TestAudience,
                    UseCertificates = true,
                    UseDevelopmentCertificates = false,
                    CertificatePath = certificatePath,
                    CertificatePassword = "irrelevant",
                }
            );
            _result = await RevokeAsAsync(manager, OwnerClientId, token);
        }

        [TearDown]
        public void RemoveScratchDirectory() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void It_reports_temporarily_unavailable_at_the_key_retrieval_boundary() =>
            _result
                .Should()
                .BeOfType<TokenRevocationResult.TemporarilyUnavailable>()
                .Which.Reason.Should()
                .Be("signing-key-retrieval");

        [Test]
        public void It_does_not_touch_the_token_store() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// A token manager whose only non-default setting is the per-client token limit, so a test
    /// asserting on that number is asserting on a configured value rather than on the default.
    /// </summary>
    private OpenIddictTokenManager CreateTokenManagerWithTokenLimit(int limit) =>
        NewTokenManager(
            // EncryptionKey has to be set for the database signing-key path to run at all; the
            // faked repository ignores its value.
            new IdentityOptions
            {
                Authority = TestIssuer,
                Audience = TestAudience,
                EncryptionKey = "test-encryption-key",
                BearerTokenPerClientLimit = limit,
            }
        );

    /// <summary>
    /// Arranges an approved client with a matching secret and a usable signing key, which is what
    /// GetAccessTokenAsync needs before it reaches StoreTokenAsync.
    /// </summary>
    private Guid ArrangeGrantableClient(bool hasNoApiClientRow = false, params string[] scopes)
    {
        Guid applicationId = Guid.NewGuid();
        ApplicationInfo application = new()
        {
            Id = applicationId,
            ClientId = GrantClientId,
            ClientSecret = "hashed-secret",
            IsApproved = true,
            ProtocolMappers = "[]",
            Scopes = scopes,
            Permissions = scopes,
        };
        if (hasNoApiClientRow)
        {
            application.HasNoApiClientRow = true;
        }

        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(GrantClientId)).Returns(application);

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
        public void It_limits_an_application_with_the_default_classification() =>
            _call.MaxActiveTokens.Should().Be(ConfiguredLimit);

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

    [TestFixture("edfi_admin_api/full_access")]
    [TestFixture("edfi_admin_api/readonly_access")]
    [TestFixture("edfi_admin_api/authMetadata_readonly_access")]
    [TestFixture(
        "edfi_admin_api/readonly_access,edfi_admin_api/full_access,edfi_admin_api/authMetadata_readonly_access"
    )]
    public class Given_GetAccessTokenAsync_WhenTheApplicationIsTokenLimitExempt(string registeredScopes)
        : OpenIddictTokenManagerTests
    {
        private StoredTokenCall _call = null!;
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            ArrangeGrantableClient(true, registeredScopes.Split(','));
            ArrangeStoreOutcome(TokenStoreOutcome.Stored, call => _call = call);

            _result = await CreateTokenManagerWithTokenLimit(3).GetAccessTokenAsync(GrantCredentials());
        }

        [Test]
        public void It_passes_the_disabling_value() => _call.MaxActiveTokens.Should().Be(-1);

        [Test]
        public void It_returns_a_success_result() => _result.Should().BeOfType<TokenResult.Success>();
    }

    [TestFixture(true, "")]
    [TestFixture(true, "EdFiSandbox")]
    [TestFixture(true, "edfi_admin_api/full_access,EdFiSandbox")]
    [TestFixture(true, "EDFI_ADMIN_API/full_access")]
    [TestFixture(true, "edfi_admin_api")]
    [TestFixture(true, "edfi_admin_api/sis")]
    [TestFixture(true, "edfi_admin_api/")]
    [TestFixture(true, "edfi_admin_api/full_access,edfi_admin_api/sis")]
    [TestFixture(false, "edfi_admin_api/full_access")]
    public class Given_GetAccessTokenAsync_WhenTheApplicationDoesNotQualifyForExemption(
        bool hasNoApiClientRow,
        string registeredScopes
    ) : OpenIddictTokenManagerTests
    {
        private StoredTokenCall _call = null!;
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            ArrangeGrantableClient(
                hasNoApiClientRow: hasNoApiClientRow,
                scopes: registeredScopes.Split(',', StringSplitOptions.RemoveEmptyEntries)
            );
            ArrangeStoreOutcome(TokenStoreOutcome.Stored, call => _call = call);
            var credentials = GrantCredentials();
            // A request cannot make an API or mixed-scope credential exempt by asking for admin scopes.
            credentials.Add(new("scope", "edfi_admin_api/full_access"));

            _result = await CreateTokenManagerWithTokenLimit(1).GetAccessTokenAsync(credentials);
        }

        [Test]
        public void It_passes_the_configured_limit() => _call.MaxActiveTokens.Should().Be(1);

        [Test]
        public void It_preserves_successful_authentication() =>
            _result.Should().BeOfType<TokenResult.Success>();
    }

    [TestFixture]
    public class Given_GetAccessTokenAsync_WhenTheDefaultTokenLimitIsExceeded : OpenIddictTokenManagerTests
    {
        private TokenResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            ArrangeGrantableClient();
            ArrangeStoreOutcome(TokenStoreOutcome.LimitExceeded, _ => { });

            OpenIddictTokenManager manager = new(
                Options.Create(new IdentityOptions { EncryptionKey = "test-encryption-key" }),
                NullLogger<OpenIddictTokenManager>.Instance,
                _secretHasher,
                _tokenRepository,
                _signingKeyProvider,
                _developmentCertificateStore
            );
            _result = await manager.GetAccessTokenAsync(GrantCredentials());
        }

        [Test]
        public void It_returns_a_token_limit_failure_carrying_fifteen() =>
            _result.Should().BeEquivalentTo(new TokenResult.FailureTokenLimitExceeded(15));
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

    // DMS-1556 step 2.2: the manager reads keys from the shared snapshot, reports dependency failures as typed
    // exceptions, and issues development-certificate tokens through the shared certificate store.

    /// <summary>A concrete <see cref="DbException"/>, which is abstract, for a store that cannot be read.</summary>
    private sealed class StoreUnavailableException(string message) : DbException(message);

    /// <summary>What an action threw, or <c>null</c> when it completed.</summary>
    private static async Task<Exception?> ExceptionFrom(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Registers one active public key and returns a token signed by its private half, with a fresh jti.
    /// </summary>
    private (string Token, Guid Jti) ArrangeTokenSignedByTheActiveKey()
    {
        var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
        StubActivePublicKey(keyId, publicKeySpki);
        var jti = Guid.NewGuid();
        return (CreateSignedToken(signingKey, [new Claim(JwtRegisteredClaimNames.Jti, jti.ToString())]), jti);
    }

    private static SigningKeySnapshot SnapshotOf(params (string KeyId, RSA Rsa)[] keys) =>
        new(
            keys.Select(key =>
                SigningKeyEntry.FromRsaPublicParameters(key.KeyId, key.Rsa.ExportParameters(false))
            ),
            DateTimeOffset.UtcNow,
            retrievedAtTimestamp: 0,
            version: 1,
            SigningKeySource.Database
        );

    private static string KeyIdOf(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid;

    private static string AccessTokenOf(TokenResult result) =>
        System
            .Text.Json.JsonDocument.Parse(result.Should().BeOfType<TokenResult.Success>().Subject.Token)
            .RootElement.GetProperty("access_token")
            .GetString()!;

    // 2.2-a: keys are projected from the snapshot, so a second read is served from memory. Before DMS-1556 every call
    // read the key table.
    [TestFixture]
    public class Given_GetPublicKeysAsync_FromTheSnapshot : OpenIddictTokenManagerTests
    {
        private RSAParameters _expected;
        private List<(RSAParameters RsaParameters, string KeyId)> _first = null!;
        private List<(RSAParameters RsaParameters, string KeyId)> _second = null!;

        [SetUp]
        public async Task Act()
        {
            using RSA rsa = RSA.Create(2048);
            _expected = rsa.ExportParameters(false);
            StubActivePublicKey("key-1", rsa.ExportSubjectPublicKeyInfo());

            _first = [.. await _tokenManager.GetPublicKeysAsync()];
            _second = [.. await _tokenManager.GetPublicKeysAsync()];
        }

        [Test]
        public void It_returns_the_snapshot_key() => _first.Select(key => key.KeyId).Should().Equal("key-1");

        [Test]
        public void It_returns_the_public_modulus() =>
            _first[0].RsaParameters.Modulus.Should().Equal(_expected.Modulus);

        [Test]
        public void It_returns_the_public_exponent() =>
            _first[0].RsaParameters.Exponent.Should().Equal(_expected.Exponent);

        [Test]
        public void It_serves_the_second_read_from_the_snapshot() =>
            _second.Select(key => key.KeyId).Should().Equal("key-1");

        [Test]
        public void It_reads_the_key_store_once() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    // 2.2-a, AC 4: a failed retrieval is an exception, never the empty list JWKS used to publish as "200 []".
    [TestFixture]
    public class Given_GetPublicKeysAsync_WhenTheKeyStoreFails : OpenIddictTokenManagerTests
    {
        private readonly StoreUnavailableException _failure = new("key store down");
        private Exception? _thrown;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .ThrowsAsync(_failure);

            _thrown = await ExceptionFrom(() => _tokenManager.GetPublicKeysAsync());
        }

        [Test]
        public void It_throws_signing_keys_unavailable() =>
            _thrown.Should().BeOfType<SigningKeysUnavailableException>();

        [Test]
        public void It_classifies_the_signing_key_store() =>
            _thrown
                .Should()
                .BeAssignableTo<AuthenticationDependencyUnavailableException>()
                .Which.Category.Should()
                .Be(AuthenticationDependencyCategory.SigningKeyStore);

        [Test]
        public void It_carries_the_store_failure() => _thrown!.InnerException.Should().BeSameAs(_failure);
    }

    // 2.2-b: a key id the snapshot already holds needs no refresh.
    [TestFixture]
    public class Given_ValidateTokenAsync_WithAKeyIdTheSnapshotHolds : OpenIddictTokenManagerTests
    {
        private ISigningKeySnapshotProvider _provider = null!;
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            using RSA rsa = RSA.Create(2048);
            _provider = A.Fake<ISigningKeySnapshotProvider>();
            SigningKeySnapshot snapshot = SnapshotOf(("key-1", rsa));
            A.CallTo(() => _provider.GetUsableAsync(A<CancellationToken>._)).Returns(snapshot);
            A.CallTo(() => _provider.Current).Returns(snapshot);

            var jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti)).Returns("valid");
            string token = CreateSignedToken(
                new RsaSecurityKey(rsa) { KeyId = "key-1" },
                [new Claim(JwtRegisteredClaimNames.Jti, jti.ToString())]
            );

            _result = await CreateConfiguredTokenManager(signingKeyProvider: _provider)
                .ValidateTokenAsync(token);
        }

        [Test]
        public void It_accepts_the_token() => _result.Should().BeTrue();

        [Test]
        public void It_requests_no_unknown_key_refresh() =>
            A.CallTo(() => _provider.TryRefreshForUnknownKeyAsync(A<string>._, A<CancellationToken>._))
                .MustNotHaveHappened();
    }

    // 2.2-b: an unknown key id gets exactly one gated refresh; still unknown after it, the token is rejected.
    [TestFixture]
    public class Given_ValidateTokenAsync_WithAKeyIdTheSnapshotLacks : OpenIddictTokenManagerTests
    {
        private ISigningKeySnapshotProvider _provider = null!;
        private ILogger<OpenIddictTokenManager> _logger = null!;
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            using RSA known = RSA.Create(2048);
            using RSA unknown = RSA.Create(2048);
            _provider = A.Fake<ISigningKeySnapshotProvider>();
            SigningKeySnapshot snapshot = SnapshotOf(("key-1", known));
            A.CallTo(() => _provider.GetUsableAsync(A<CancellationToken>._)).Returns(snapshot);
            A.CallTo(() => _provider.Current).Returns(snapshot);
            A.CallTo(() => _provider.TryRefreshForUnknownKeyAsync(A<string>._, A<CancellationToken>._))
                .Returns(SigningKeyUnknownKeyOutcome.RefreshedAbsent);

            _logger = A.Fake<ILogger<OpenIddictTokenManager>>();
            A.CallTo(() => _logger.IsEnabled(A<LogLevel>._)).Returns(true);

            string token = CreateSignedToken(
                new RsaSecurityKey(unknown) { KeyId = "unknown-key" },
                [new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())]
            );

            _result = await CreateConfiguredTokenManager(_logger, _provider).ValidateTokenAsync(token);
        }

        [Test]
        public void It_rejects_the_token() => _result.Should().BeFalse();

        [Test]
        public void It_requests_one_refresh_for_the_key_id() =>
            A.CallTo(() => _provider.TryRefreshForUnknownKeyAsync("unknown-key", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_does_not_query_token_status() =>
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_warns_with_the_key_id_and_the_outcome() =>
            LogMessagesAt(_logger, LogLevel.Warning)
                .Should()
                .Contain(
                    "Token key id unknown-key was not in the signing-key snapshot; unknown-key refresh outcome: RefreshedAbsent"
                );
    }

    /// <summary>
    /// Validates a token signed by the old key, which loads the snapshot; inserts a newer active key; advances fake
    /// time by <paramref name="advance"/>; then validates a token signed by the new key. Returns whether the new-key
    /// token was accepted.
    /// </summary>
    private async Task<bool> RotateAKeyInAndValidateAsync(TimeSpan advance)
    {
        FakeTimeProvider time = SigningKeyTestSupport.NewTime();
        using RSA oldKey = RSA.Create(2048);
        using RSA newKey = RSA.Create(2048);
        List<PublicKeyInfo> activeKeys =
        [
            new() { KeyId = "old-key", PublicKey = oldKey.ExportSubjectPublicKeyInfo() },
        ];
        A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult<IEnumerable<PublicKeyInfo>>([.. activeKeys]));
        A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).Returns("valid");

        using SigningKeySnapshotProvider provider = new(
            new DatabaseSigningKeySource(_tokenRepository, NullLogger<DatabaseSigningKeySource>.Instance),
            Options.Create(new IdentityOptions()),
            time,
            NullLogger<SigningKeySnapshotProvider>.Instance
        );
        OpenIddictTokenManager manager = CreateConfiguredTokenManager(signingKeyProvider: provider);
        Claim[] claims = [new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())];

        (
            await manager.ValidateTokenAsync(
                CreateSignedToken(new RsaSecurityKey(oldKey) { KeyId = "old-key" }, claims)
            )
        )
            .Should()
            .BeTrue("the old key is in the first snapshot");

        activeKeys.Add(new() { KeyId = "new-key", PublicKey = newKey.ExportSubjectPublicKeyInfo() });
        time.Advance(advance);

        return await manager.ValidateTokenAsync(
            CreateSignedToken(new RsaSecurityKey(newKey) { KeyId = "new-key" }, claims)
        );
    }

    // 2.2-b, rotation eligible: past the unknown-key cooldown (30 s by default), the first token carrying the new key id
    // refreshes the snapshot and is accepted in the same call.
    [TestFixture]
    public class Given_ValidateTokenAsync_ForAKeyRotatedInAfterTheCooldown : OpenIddictTokenManagerTests
    {
        private bool _accepted;

        [SetUp]
        public async Task Act() => _accepted = await RotateAKeyInAndValidateAsync(TimeSpan.FromSeconds(31));

        [Test]
        public void It_accepts_the_new_key_on_first_sighting() => _accepted.Should().BeTrue();

        [Test]
        public void It_reads_the_key_store_for_the_refresh() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
    }

    // 2.2-b, rotation suppressed: inside the cooldown the new key id gets no load and the token is rejected.
    [TestFixture]
    public class Given_ValidateTokenAsync_ForAKeyRotatedInDuringTheCooldown : OpenIddictTokenManagerTests
    {
        private bool _accepted;

        [SetUp]
        public async Task Act() => _accepted = await RotateAKeyInAndValidateAsync(TimeSpan.FromSeconds(10));

        [Test]
        public void It_rejects_the_new_key_token() => _accepted.Should().BeFalse();

        [Test]
        public void It_does_not_read_the_key_store_again() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    // 2.2-c, D-7: a status store that cannot be read is a dependency failure, not a verdict. Reported as false it would
    // be answered with 401 and indistinguishable from a revoked token.
    [TestFixture("database")]
    [TestFixture("timeout")]
    [TestFixture("canceled")]
    public class Given_ValidateTokenAsync_WhenTheTokenStatusStoreFails(string failure)
        : OpenIddictTokenManagerTests
    {
        private Exception _failure = null!;
        private Exception? _thrown;

        [SetUp]
        public async Task Act()
        {
            _failure = failure switch
            {
                "database" => new StoreUnavailableException("status store down"),
                "timeout" => new TimeoutException("status read timed out"),
                _ => new OperationCanceledException("status read canceled"),
            };
            var (token, jti) = ArrangeTokenSignedByTheActiveKey();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti)).ThrowsAsync(_failure);

            _thrown = await ExceptionFrom(() => CreateConfiguredTokenManager().ValidateTokenAsync(token));
        }

        [Test]
        public void It_throws_the_dependency_exception() =>
            _thrown.Should().BeOfType<AuthenticationDependencyUnavailableException>();

        [Test]
        public void It_classifies_the_token_status_store() =>
            ((AuthenticationDependencyUnavailableException)_thrown!)
                .Category.Should()
                .Be(AuthenticationDependencyCategory.TokenStatusStore);

        [Test]
        public void It_carries_the_store_failure() => _thrown!.InnerException.Should().BeSameAs(_failure);
    }

    // 2.2-c: a store that already reports itself unavailable (the SQL Server pool-exhaustion translation) passes through
    // unchanged, neither re-wrapped nor turned into false.
    [TestFixture]
    public class Given_ValidateTokenAsync_WhenTheStatusStoreReportsItselfUnavailable
        : OpenIddictTokenManagerTests
    {
        private readonly AuthenticationDependencyUnavailableException _failure = new(
            AuthenticationDependencyCategory.TokenStatusStore,
            "The token status store could not be read.",
            new InvalidOperationException("pool timeout")
        );
        private Exception? _thrown;

        [SetUp]
        public async Task Act()
        {
            var (token, jti) = ArrangeTokenSignedByTheActiveKey();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti)).ThrowsAsync(_failure);

            _thrown = await ExceptionFrom(() => CreateConfiguredTokenManager().ValidateTokenAsync(token));
        }

        [Test]
        public void It_rethrows_the_store_exception() => _thrown.Should().BeSameAs(_failure);
    }

    /// <summary>
    /// Loads a snapshot holding one key at fake time zero and advances time by <paramref name="age"/>. Every later key
    /// read advances time by <paramref name="readDuration"/> and then fails. Then it validates a token whose key id the
    /// snapshot lacks, and returns what validation returned or threw and how often the key store was read.
    /// </summary>
    private async Task<(
        bool? Result,
        Exception? Thrown,
        int Reads
    )> ValidateAnUnknownKeyAcrossAFailedRefreshAsync(TimeSpan age, TimeSpan readDuration)
    {
        FakeTimeProvider time = SigningKeyTestSupport.NewTime();
        using RSA known = RSA.Create(2048);
        using RSA unknown = RSA.Create(2048);
        int[] reads = [0];
        A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                if (Interlocked.Increment(ref reads[0]) == 1)
                {
                    return Task.FromResult<IEnumerable<PublicKeyInfo>>([
                        new PublicKeyInfo
                        {
                            KeyId = "known-key",
                            PublicKey = known.ExportSubjectPublicKeyInfo(),
                        },
                    ]);
                }

                time.Advance(readDuration);
                return Task.FromException<IEnumerable<PublicKeyInfo>>(
                    new StoreUnavailableException("key store down")
                );
            });

        using SigningKeySnapshotProvider provider = new(
            new DatabaseSigningKeySource(_tokenRepository, NullLogger<DatabaseSigningKeySource>.Instance),
            Options.Create(new IdentityOptions()),
            time,
            NullLogger<SigningKeySnapshotProvider>.Instance
        );
        (await provider.GetUsableAsync(CancellationToken.None))
            .ContainsKeyId("known-key")
            .Should()
            .BeTrue("the first read publishes the known key");
        time.Advance(age);

        string token = CreateSignedToken(
            new RsaSecurityKey(unknown) { KeyId = "unknown-key" },
            [new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())]
        );
        OpenIddictTokenManager manager = CreateConfiguredTokenManager(signingKeyProvider: provider);
        try
        {
            return (await manager.ValidateTokenAsync(token), null, reads[0]);
        }
        catch (Exception exception)
        {
            return (null, exception, reads[0]);
        }
    }

    // Past the cooldown the unknown key id gets its refresh, which fails. The snapshot is still fresh, so the token is
    // rejected as an unknown key rather than reported as an outage.
    [TestFixture]
    public class Given_ValidateTokenAsync_WhenAnUnknownKeyRefreshFailsWithKeysStillUsable
        : OpenIddictTokenManagerTests
    {
        private (bool? Result, Exception? Thrown, int Reads) _outcome;

        [SetUp]
        public async Task Act() =>
            _outcome = await ValidateAnUnknownKeyAcrossAFailedRefreshAsync(
                age: TimeSpan.FromSeconds(31),
                readDuration: TimeSpan.Zero
            );

        [Test]
        public void It_rejects_the_token() => _outcome.Result.Should().BeFalse();

        [Test]
        public void It_throws_nothing() => _outcome.Thrown.Should().BeNull();

        [Test]
        public void It_reads_the_key_store_for_the_refresh() => _outcome.Reads.Should().Be(2);

        [Test]
        public void It_does_not_query_token_status() =>
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
    }

    // One second short of the maximum staleness the snapshot is usable, and the refresh its unknown key id joins takes
    // two seconds and fails. The snapshot is then expired, so verification reports the keys unavailable. Before this
    // correction it verified against the expired snapshot and answered false, an invalid token.
    [TestFixture]
    public class Given_ValidateTokenAsync_WhenAFailedUnknownKeyRefreshCrossesTheMaximumStaleness
        : OpenIddictTokenManagerTests
    {
        private (bool? Result, Exception? Thrown, int Reads) _outcome;

        [SetUp]
        public async Task Act() =>
            _outcome = await ValidateAnUnknownKeyAcrossAFailedRefreshAsync(
                age: TimeSpan.FromSeconds(3599),
                readDuration: TimeSpan.FromSeconds(2)
            );

        [Test]
        public void It_throws_signing_keys_unavailable() =>
            _outcome.Thrown.Should().BeOfType<SigningKeysUnavailableException>();

        [Test]
        public void It_reports_the_snapshot_expired() =>
            ((SigningKeysUnavailableException)_outcome.Thrown!)
                .Reason.Should()
                .Be(SigningKeysUnavailableReason.SnapshotExpired);

        [Test]
        public void It_makes_no_further_key_read() => _outcome.Reads.Should().Be(2);

        [Test]
        public void It_does_not_query_token_status() =>
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
    }

    // 2.2-c: only store failures are translated; any other exception keeps the existing invalid-token answer.
    [TestFixture]
    public class Given_ValidateTokenAsync_WhenTheStatusReadFailsForAnotherReason : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (token, jti) = ArrangeTokenSignedByTheActiveKey();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti))
                .ThrowsAsync(new InvalidOperationException("unexpected"));

            _result = await CreateConfiguredTokenManager().ValidateTokenAsync(token);
        }

        [Test]
        public void It_rejects_the_token() => _result.Should().BeFalse();
    }

    // 2.2-c: no usable snapshot is the signing-key dependency failure, rethrown ahead of the general catch.
    [TestFixture]
    public class Given_ValidateTokenAsync_WhenNoSigningKeysCanBeLoaded : OpenIddictTokenManagerTests
    {
        private Exception? _thrown;

        [SetUp]
        public async Task Act()
        {
            var (token, _) = ArrangeTokenSignedByTheActiveKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .ThrowsAsync(new StoreUnavailableException("key store down"));

            _thrown = await ExceptionFrom(() => CreateConfiguredTokenManager().ValidateTokenAsync(token));
        }

        [Test]
        public void It_throws_signing_keys_unavailable() =>
            _thrown.Should().BeOfType<SigningKeysUnavailableException>();

        [Test]
        public void It_does_not_query_token_status() =>
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
    }

    // 2.2-d, §4.7: introspection keeps answering an undecided token as inactive, and says why at Error.
    [TestFixture]
    public class Given_EnhancedTokenValidator_WhenTheTokenStatusStoreFails : OpenIddictTokenManagerTests
    {
        private ILogger<EnhancedTokenValidator> _logger = null!;
        private TokenValidationResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            _logger = A.Fake<ILogger<EnhancedTokenValidator>>();
            A.CallTo(() => _logger.IsEnabled(A<LogLevel>._)).Returns(true);
            var (token, jti) = ArrangeTokenSignedByTheActiveKey();
            A.CallTo(() => _tokenRepository.GetTokenStatusAsync(jti))
                .ThrowsAsync(new StoreUnavailableException("status store down"));

            _result = await new EnhancedTokenValidator(
                CreateConfiguredTokenManager(),
                _logger
            ).ValidateTokenAsync(token);
        }

        [Test]
        public void It_reports_the_token_invalid() => _result.IsValid.Should().BeFalse();

        [Test]
        public void It_creates_no_principal() => _result.Principal.Should().BeNull();

        [Test]
        public void It_logs_the_category_at_error() =>
            SigningKeyTestSupport
                .MessagesAt(_logger, LogLevel.Error)
                .Should()
                .Equal("Token validation could not reach a decision: the TokenStatusStore is unavailable");
    }

    /// <summary>
    /// Self-contained certificate mode with a development certificate that does not exist yet, in its own temporary
    /// directory. The manager, the certificate source and the snapshot provider share one store, as the DI
    /// registration wires them.
    /// </summary>
    private sealed class DevelopmentCertificateFirstStart : IDisposable
    {
        private readonly string _directory = Directory
            .CreateTempSubdirectory("dms1556-manager-devcert-")
            .FullName;

        public DevelopmentCertificateFirstStart()
        {
            Path = System.IO.Path.Combine(_directory, "devcert.pfx");
            Options = new IdentityOptions
            {
                Authority = TestIssuer,
                Audience = TestAudience,
                UseCertificates = true,
                UseDevelopmentCertificates = true,
                DevCertificatePath = Path,
                DevCertificatePassword = "password",
            };
            StoreLogger = A.Fake<ILogger<DevelopmentCertificateStore>>();
            A.CallTo(() => StoreLogger.IsEnabled(A<LogLevel>._)).Returns(true);
            Store = new DevelopmentCertificateStore(
                Microsoft.Extensions.Options.Options.Create(Options),
                StoreLogger
            );
            Provider = new SigningKeySnapshotProvider(
                new CertificateSigningKeySource(Microsoft.Extensions.Options.Options.Create(Options), Store),
                Microsoft.Extensions.Options.Options.Create(Options),
                TimeProvider.System,
                NullLogger<SigningKeySnapshotProvider>.Instance
            );
        }

        public string Path { get; }

        public IdentityOptions Options { get; }

        public ILogger<DevelopmentCertificateStore> StoreLogger { get; }

        public DevelopmentCertificateStore Store { get; }

        public SigningKeySnapshotProvider Provider { get; }

        public IReadOnlyList<string> FilesInDirectory =>
            [.. Directory.GetFiles(_directory).Select(file => System.IO.Path.GetFileName(file))];

        public void Dispose()
        {
            Provider.Dispose();
            Store.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A client that can mint, whose stored tokens all read back as "valid".</summary>
    private void ArrangeMintingClientWithValidTokens()
    {
        ArrangeGrantableClient();
        ArrangeStoreOutcome(TokenStoreOutcome.Stored, _ => { });
        A.CallTo(() => _tokenRepository.GetTokenStatusAsync(A<Guid>._)).Returns("valid");
    }

    // 2.2-e, D-10: issuance creates the missing development certificate through the shared store, not on its own.
    // Before DMS-1556 issuance wrote the file itself, so the store never logged a creation.
    [TestFixture]
    public class Given_GetAccessTokenAsync_FirstOnADevelopmentCertificateFirstStart
        : OpenIddictTokenManagerTests
    {
        private DevelopmentCertificateFirstStart _start = null!;
        private string _issuedKeyId = null!;
        private string _publishedKeyId = null!;
        private bool _validated;

        [SetUp]
        public async Task Act()
        {
            _start = new DevelopmentCertificateFirstStart();
            ArrangeMintingClientWithValidTokens();
            OpenIddictTokenManager manager = NewTokenManager(
                _start.Options,
                signingKeyProvider: _start.Provider,
                developmentCertificateStore: _start.Store
            );

            string token = AccessTokenOf(await manager.GetAccessTokenAsync(GrantCredentials()));
            _issuedKeyId = KeyIdOf(token);
            _publishedKeyId = (await manager.GetPublicKeysAsync()).Single().KeyId;
            _validated = await manager.ValidateTokenAsync(token);
        }

        [TearDown]
        public void TearDown() => _start.Dispose();

        [Test]
        public void It_creates_the_certificate_through_the_shared_store() =>
            SigningKeyTestSupport
                .MessagesAt(_start.StoreLogger, LogLevel.Information)
                .Should()
                .Equal($"Development certificate created at {_start.Path}");

        [Test]
        public void It_issues_with_the_published_key_id() => _issuedKeyId.Should().Be(_publishedKeyId);

        [Test]
        public void It_validates_the_issued_token() => _validated.Should().BeTrue();
    }

    // 2.2-e, I-10: on a first start the refresh service, token issuance and validation all race to the missing
    // development certificate. One certificate is created, and every issued token verifies against it.
    [TestFixture]
    public class Given_a_development_certificate_first_start_with_concurrent_mint_validation_and_refresh
        : OpenIddictTokenManagerTests
    {
        private const int Callers = 16;

        private DevelopmentCertificateFirstStart _start = null!;
        private List<string> _issuedKeyIds = null!;
        private List<string> _publishedKeyIds = null!;
        private List<bool> _validated = null!;

        [SetUp]
        public async Task Act()
        {
            _start = new DevelopmentCertificateFirstStart();
            ArrangeMintingClientWithValidTokens();
            OpenIddictTokenManager manager = NewTokenManager(
                _start.Options,
                signingKeyProvider: _start.Provider,
                developmentCertificateStore: _start.Store
            );
            using SigningKeyRefreshService refreshService = new(
                _start.Provider,
                Options.Create(_start.Options),
                TimeProvider.System,
                NullLogger<SigningKeyRefreshService>.Instance
            );

            using Barrier start = new((2 * Callers) + 1);
            Task refresh = Task.Run(() =>
            {
                start.SignalAndWait();
                return refreshService.StartAsync(CancellationToken.None);
            });
            Task<TokenResult>[] mints =
            [
                .. Enumerable
                    .Range(0, Callers)
                    .Select(_ =>
                        Task.Run(() =>
                        {
                            start.SignalAndWait();
                            return manager.GetAccessTokenAsync(GrantCredentials());
                        })
                    ),
            ];
            Task<IEnumerable<(RSAParameters RsaParameters, string KeyId)>>[] validators =
            [
                .. Enumerable
                    .Range(0, Callers)
                    .Select(_ =>
                        Task.Run(() =>
                        {
                            start.SignalAndWait();
                            return manager.GetPublicKeysAsync();
                        })
                    ),
            ];

            TimeSpan guard = TimeSpan.FromSeconds(30);
            await refresh.WaitAsync(guard);
            List<string> tokens = [.. (await Task.WhenAll(mints).WaitAsync(guard)).Select(AccessTokenOf)];
            _publishedKeyIds =
            [
                .. (await Task.WhenAll(validators).WaitAsync(guard)).Select(keys => keys.Single().KeyId),
            ];
            _issuedKeyIds = [.. tokens.Select(KeyIdOf)];
            _validated = [];
            foreach (string token in tokens)
            {
                _validated.Add(await manager.ValidateTokenAsync(token));
            }

            await refreshService.StopAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _start.Dispose();

        [Test]
        public void It_creates_the_certificate_once() =>
            SigningKeyTestSupport
                .MessagesAt(_start.StoreLogger, LogLevel.Information)
                .Should()
                .Equal($"Development certificate created at {_start.Path}");

        [Test]
        public void It_leaves_only_the_certificate_file() =>
            _start.FilesInDirectory.Should().Equal("devcert.pfx");

        [Test]
        public void It_publishes_one_key_id() => _publishedKeyIds.Distinct().Should().ContainSingle();

        [Test]
        public void It_issues_every_token_with_the_published_key_id() =>
            _issuedKeyIds.Should().HaveCount(Callers).And.OnlyContain(keyId => keyId == _publishedKeyIds[0]);

        [Test]
        public void It_validates_every_issued_token() =>
            _validated.Should().HaveCount(Callers).And.OnlyContain(valid => valid);
    }
}
