// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
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

    [TestFixture]
    public class Given_AuthenticateClientAsync_WithValidCredentials : OpenIddictTokenManagerTests
    {
        private string? _result;

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
            A.CallTo(() => _secretHasher.VerifySecretAsync("plain-secret", "hashed-secret")).Returns(true);

            _result = await _tokenManager.AuthenticateClientAsync("known-client", "plain-secret");
        }

        [Test]
        public void It_returns_the_client_id() => _result.Should().Be("known-client");
    }

    // The case that motivated returning a client id instead of a flag: SQL Server's default
    // collation resolves a mis-cased client id, so the caller authenticates under a spelling
    // that was never stored. Handing that spelling to the ownership check would compare it
    // against the canonical value the token was minted from and find a stranger.
    [TestFixture]
    public class Given_AuthenticateClientAsync_WithNonCanonicalCasing : OpenIddictTokenManagerTests
    {
        private string? _result;

        [SetUp]
        public async Task Act()
        {
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

            _result = await _tokenManager.AuthenticateClientAsync("KNOWN-Client", "plain-secret");
        }

        [Test]
        public void It_returns_the_canonical_client_id_not_the_requested_one() =>
            _result.Should().Be("known-client");
    }

    // A stored row with no client id cannot yield a canonical value, and returning an empty
    // one would authenticate a caller into an ownership check no token can ever match. The
    // requested spelling is used instead, matching what minting does in the same situation.
    [TestFixture]
    public class Given_AuthenticateClientAsync_WhenTheStoredClientIdIsEmpty : OpenIddictTokenManagerTests
    {
        private string? _result;

        [SetUp]
        public async Task Act()
        {
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

            _result = await _tokenManager.AuthenticateClientAsync("known-client", "plain-secret");
        }

        [Test]
        public void It_falls_back_to_the_requested_client_id() => _result.Should().Be("known-client");
    }

    [TestFixture]
    public class Given_AuthenticateClientAsync_WhenClientIsUnknown : OpenIddictTokenManagerTests
    {
        private string? _result;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("unknown-client"))
                .Returns((ApplicationInfo?)null);

            _result = await _tokenManager.AuthenticateClientAsync("unknown-client", "plain-secret");
        }

        [Test]
        public void It_returns_null() => _result.Should().BeNull();
    }

    [TestFixture]
    public class Given_AuthenticateClientAsync_WhenSecretIsInvalid : OpenIddictTokenManagerTests
    {
        private string? _result;

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

            _result = await _tokenManager.AuthenticateClientAsync("known-client", "wrong-secret");
        }

        [Test]
        public void It_returns_null() => _result.Should().BeNull();
    }

    [TestFixture]
    public class Given_AuthenticateClientAsync_WhenApiClientIsNotApproved : OpenIddictTokenManagerTests
    {
        private string? _result;

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

            _result = await _tokenManager.AuthenticateClientAsync("disabled-client", "plain-secret");
        }

        [Test]
        public void It_returns_null() => _result.Should().BeNull();
    }

    [TestFixture]
    public class Given_AuthenticateClientAsync_WithMissingCredentials : OpenIddictTokenManagerTests
    {
        private string? _result;

        [SetUp]
        public async Task Act() =>
            _result = await _tokenManager.AuthenticateClientAsync(string.Empty, string.Empty);

        [Test]
        public void It_returns_null() => _result.Should().BeNull();

        [Test]
        public void It_does_not_query_the_repository() =>
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(A<string>._)).MustNotHaveHappened();
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

    [TestFixture]
    public class Given_RevokeTokenAsync_WithAValidJtiOwnedByTheCaller : OpenIddictTokenManagerTests
    {
        private bool _result;
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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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
        private bool _result;

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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OtherClientId);
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
    public class Given_RevokeTokenAsync_WithATokenCarryingNoClientId : OpenIddictTokenManagerTests
    {
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(
                signingKey,
                new[] { new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) }
            );

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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

    // The ownership check is only worth anything if the target token's signature is verified
    // first: otherwise a caller could forge a token naming its own client_id while embedding a
    // victim's jti, and revoke the victim's token. This fixture is exactly that attack.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenForgedToNameTheCaller : OpenIddictTokenManagerTests
    {
        private bool _result;
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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(forgedToken, OwnerClientId);
        }

        [Test]
        public void It_returns_false()
        {
            _result.Should().BeFalse();
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
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);

            string token = CreateSignedToken(signingKey, new[] { new Claim("client_id", OwnerClientId) });

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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
    public class Given_RevokeTokenAsync_WithoutACallerClientId : OpenIddictTokenManagerTests
    {
        private bool _result;

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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, string.Empty);
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

    // An empty client_id claim and a missing one reach the ownership comparison by different
    // routes — FirstOrDefault finds a claim whose value is "" versus finding no claim at all —
    // even though both must end in the same no-op.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenCarryingAnEmptyClientId : OpenIddictTokenManagerTests
    {
        private bool _result;

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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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

    // The ownership comparison is deliberately case-sensitive (StringComparison.Ordinal).
    // Switching it to OrdinalIgnoreCase would make the ownership boundary depend on the deployed
    // database engine's collation, so this fixture exists to fail loudly if anyone loosens it.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenWhoseClientIdDiffersOnlyByCase : OpenIddictTokenManagerTests
    {
        private bool _result;

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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, "owner-client");
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

    // Issuer and audience verification is load-bearing for the ownership boundary, not just
    // signature verification: a token minted by a different issuer (or for a different audience)
    // could carry any client_id it liked. These two fixtures sign with the service's own
    // registered key so that only the issuer/audience claim is wrong.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithATokenFromAnotherIssuer : OpenIddictTokenManagerTests
    {
        private bool _result;

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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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
    public class Given_RevokeTokenAsync_WithATokenForAnotherAudience : OpenIddictTokenManagerTests
    {
        private bool _result;

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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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

    // Verification includes the lifetime check, so revoking an already-expired token is a no-op.
    // That is a real behavior change from the previous unvalidated ReadJwtToken path, which would
    // have marked it revoked by jti. Pinned here so it stays a deliberate decision: a caller
    // tidying up an old token gets 200 OK while nothing is written.
    [TestFixture]
    public class Given_RevokeTokenAsync_WithAnExpiredOwnedToken : OpenIddictTokenManagerTests
    {
        private bool _result;

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

            _result = await CreateConfiguredTokenManager().RevokeTokenAsync(token, OwnerClientId);
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

    // An expired token and a forged one both fail verification, but they mean completely
    // different things: the first is routine, the second is the attack signal this change
    // exists to surface. These two fixtures assert the observed log levels differ, so the
    // distinction cannot silently regress into a single undifferentiated severity.
    [TestFixture]
    public class Given_RevokeTokenAsync_LoggingForAnExpiredOwnedToken : OpenIddictTokenManagerTests
    {
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();

            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                },
                expired: true
            );

            await CreateConfiguredTokenManager(_fakeLogger).RevokeTokenAsync(token, OwnerClientId);
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
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_LoggingForAnUntrustedToken : OpenIddictTokenManagerTests
    {
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

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

            string token = CreateSignedToken(
                attackerKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                }
            );

            await CreateConfiguredTokenManager(_fakeLogger).RevokeTokenAsync(token, OwnerClientId);
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
    }

    // An Authority or Audience typo fails every token in the environment at once. If that shared
    // the forgery message, the resulting storm would either read as an attack or drown a real one,
    // so issuer/audience rejection is reported as its own category pointing at configuration.
    [TestFixture]
    public class Given_RevokeTokenAsync_LoggingForATokenWithTheWrongAudience : OpenIddictTokenManagerTests
    {
        private ILogger<OpenIddictTokenManager> _fakeLogger = null!;

        [SetUp]
        public async Task Act()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            StubActivePublicKey(keyId, publicKeySpki);
            _fakeLogger = A.Fake<ILogger<OpenIddictTokenManager>>();

            // Correctly signed by the service's own key; only the audience is unacceptable.
            string token = CreateSignedToken(
                signingKey,
                new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("client_id", OwnerClientId),
                },
                audience: "some-other-service"
            );

            await CreateConfiguredTokenManager(_fakeLogger).RevokeTokenAsync(token, OwnerClientId);
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
        private bool _result;
        private string _callerClientId = null!;

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(targetJti)).Returns(true);

            _result = await CreateMintingTokenManager().RevokeTokenAsync(targetToken, _callerClientId);
        }

        [Test]
        public void It_revokes_the_token()
        {
            _result.Should().BeTrue();
        }

        [Test]
        public void It_treats_both_casings_as_the_same_client()
        {
            _callerClientId.Should().Be(CanonicalClientId);
        }
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

    // 2.2-d: revocation keeps its RFC 7009 contract (the endpoint answers 200) and logs the category at Error.
    [TestFixture]
    public class Given_RevokeTokenAsync_WhenNoSigningKeysCanBeLoaded : OpenIddictTokenManagerTests
    {
        private ILogger<OpenIddictTokenManager> _logger = null!;
        private bool _result;

        [SetUp]
        public async Task Act()
        {
            _logger = A.Fake<ILogger<OpenIddictTokenManager>>();
            A.CallTo(() => _logger.IsEnabled(A<LogLevel>._)).Returns(true);
            var (token, _) = ArrangeTokenSignedByTheActiveKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .ThrowsAsync(new StoreUnavailableException("key store down"));

            _result = await CreateConfiguredTokenManager(_logger).RevokeTokenAsync(token, OwnerClientId);
        }

        [Test]
        public void It_reports_no_revocation() => _result.Should().BeFalse();

        [Test]
        public void It_does_not_revoke() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_logs_the_category_at_error() =>
            LogMessagesAt(_logger, LogLevel.Error)
                .Should()
                .Equal("Failed to revoke token: the SigningKeyStore is unavailable");
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
