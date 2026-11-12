// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Token;

/// <summary>
/// The CMS verification helper over one registered RSA key, as the signing-key snapshot serves it: only the public half,
/// under a known <c>kid</c>. Every token carries the expected issuer, audience and lifetime and a <c>jti</c>, so the
/// signing algorithm (or the one failure a control fixture introduces) is the only thing that varies.
/// </summary>
public class JwtTokenValidatorTests
{
    private const string Issuer = "https://cms.example.test";
    private const string Audience = "ed-fi-cms-tests";
    private const string KeyId = "registered-key";

    /// <summary>A signing key pair and the dictionary that registers only its public half under <see cref="KeyId"/>.</summary>
    private static (RSA Signer, IDictionary<string, SecurityKey> PublicKeys) RegisterKey()
    {
        RSA signer = RSA.Create(2048);
        RSA publicHalf = RSA.Create();
        publicHalf.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
        return (
            signer,
            new Dictionary<string, SecurityKey> { [KeyId] = new RsaSecurityKey(publicHalf) { KeyId = KeyId } }
        );
    }

    /// <summary>A token signed by <paramref name="signer"/> with <paramref name="algorithm"/>.</summary>
    private static string Sign(
        RSA signer,
        string algorithm,
        string? kid = KeyId,
        string issuer = Issuer,
        string audience = Audience,
        bool expired = false
    )
    {
        DateTime now = DateTime.UtcNow;
        JwtSecurityToken jwt = new(
            issuer: issuer,
            audience: audience,
            claims: [new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
            notBefore: expired ? now.AddMinutes(-30) : now.AddMinutes(-5),
            expires: expired ? now.AddMinutes(-20) : now.AddMinutes(10),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(signer) { KeyId = kid }, algorithm)
        );
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static TokenVerification Validate(string token, IDictionary<string, SecurityKey> publicKeys) =>
        JwtTokenValidator.ValidateToken(token, publicKeys, Issuer, Audience);

    [TestFixture]
    public class Given_A_Trusted_Key_With_A_Disallowed_Algorithm
    {
        private static readonly string[] _algorithms =
        [
            SecurityAlgorithms.RsaSha256,
            SecurityAlgorithms.RsaSha512,
            SecurityAlgorithms.RsaSsaPssSha256,
        ];

        private readonly Dictionary<string, string> _tokens = [];
        private readonly Dictionary<string, TokenVerification> _results = [];
        private readonly Dictionary<string, bool> _acceptedWithoutAnAllowlist = [];
        private TokenVerification _expiredRs512 = null!;

        [SetUp]
        public void Act()
        {
            (RSA signer, IDictionary<string, SecurityKey> publicKeys) = RegisterKey();

            foreach (string algorithm in _algorithms)
            {
                string token = Sign(signer, algorithm);
                _tokens[algorithm] = token;
                _results[algorithm] = Validate(token, publicKeys);
                _acceptedWithoutAnAllowlist[algorithm] = AcceptedWithoutAnAllowlist(token, publicKeys[KeyId]);
            }

            _expiredRs512 = Validate(Sign(signer, SecurityAlgorithms.RsaSha512, expired: true), publicKeys);
        }

        /// <summary>
        /// The helper's own checks without an algorithm allowlist: same key, issuer, audience, lifetime and skew. A
        /// token this accepts is correctly signed by the registered key and otherwise valid.
        /// </summary>
        private static bool AcceptedWithoutAnAllowlist(string token, SecurityKey key)
        {
            try
            {
                new JwtSecurityTokenHandler().ValidateToken(
                    token,
                    new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = key,
                        ValidateIssuer = true,
                        ValidIssuer = Issuer,
                        ValidateAudience = true,
                        ValidAudience = Audience,
                        ValidateLifetime = true,
                        ClockSkew = JwtTokenValidator.TokenValidationClockSkew,
                    },
                    out _
                );
                return true;
            }
            catch (SecurityTokenException)
            {
                return false;
            }
        }

        [TestCase(SecurityAlgorithms.RsaSha256)]
        [TestCase(SecurityAlgorithms.RsaSha512)]
        [TestCase(SecurityAlgorithms.RsaSsaPssSha256)]
        public void It_signs_each_token_with_its_named_algorithm(string algorithm) =>
            new JwtSecurityTokenHandler().ReadJwtToken(_tokens[algorithm]).Header.Alg.Should().Be(algorithm);

        [TestCase(SecurityAlgorithms.RsaSha256)]
        [TestCase(SecurityAlgorithms.RsaSha512)]
        [TestCase(SecurityAlgorithms.RsaSsaPssSha256)]
        public void It_signs_each_token_correctly_with_the_registered_key(string algorithm) =>
            _acceptedWithoutAnAllowlist[algorithm].Should().BeTrue();

        [Test]
        public void It_accepts_the_RS256_token()
        {
            TokenVerification result = _results[SecurityAlgorithms.RsaSha256];
            result.Failure.Should().Be(TokenVerificationFailure.None);
            result.Token.Should().NotBeNull();
            result.Token!.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
            result.Detail.Should().BeEmpty();
        }

        [TestCase(SecurityAlgorithms.RsaSha512)]
        [TestCase(SecurityAlgorithms.RsaSsaPssSha256)]
        public void It_rejects_the_disallowed_algorithm_as_untrusted(string algorithm)
        {
            TokenVerification result = _results[algorithm];
            result.Failure.Should().Be(TokenVerificationFailure.Untrusted);
            result.Token.Should().BeNull();
            result.Detail.Should().NotBeEmpty();
        }

        // Order of operations: the algorithm is part of the signature check, which runs before the lifetime check, so
        // a disallowed-algorithm token cannot land in the routine expiry bucket.
        [Test]
        public void It_rejects_an_expired_disallowed_algorithm_token_as_untrusted_rather_than_expired()
        {
            _expiredRs512.Failure.Should().Be(TokenVerificationFailure.Untrusted);
            _expiredRs512.Token.Should().BeNull();
        }
    }

    // Controls: with the allowed algorithm, every other failure keeps its existing category.
    [TestFixture]
    public class Given_RS256_Tokens_That_Fail_For_Other_Reasons
    {
        private TokenVerification _expired = null!;
        private TokenVerification _wrongIssuer = null!;
        private TokenVerification _wrongAudience = null!;
        private TokenVerification _unsigned = null!;
        private TokenVerification _unknownKid = null!;
        private TokenVerification _missingKid = null!;
        private TokenVerification _otherKey = null!;

        [SetUp]
        public void Act()
        {
            (RSA signer, IDictionary<string, SecurityKey> publicKeys) = RegisterKey();
            const string Rs256 = SecurityAlgorithms.RsaSha256;

            _expired = Validate(Sign(signer, Rs256, expired: true), publicKeys);
            _wrongIssuer = Validate(Sign(signer, Rs256, issuer: "https://other.example.test"), publicKeys);
            _wrongAudience = Validate(Sign(signer, Rs256, audience: "other-audience"), publicKeys);
            _unsigned = Validate(UnsignedTokenNamingTheRegisteredKey(), publicKeys);
            _unknownKid = Validate(Sign(signer, Rs256, kid: "unknown-key"), publicKeys);
            _missingKid = Validate(Sign(signer, Rs256, kid: null), publicKeys);
            _otherKey = Validate(Sign(RSA.Create(2048), Rs256), publicKeys);
        }

        /// <summary>An <c>alg: none</c> token whose <c>kid</c> names the registered key, so it reaches the signature check.</summary>
        private static string UnsignedTokenNamingTheRegisteredKey()
        {
            DateTime now = DateTime.UtcNow;
            JwtHeader header = new() { [JwtHeaderParameterNames.Kid] = KeyId };
            JwtPayload payload = new(
                Issuer,
                Audience,
                [new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
                now.AddMinutes(-5),
                now.AddMinutes(10)
            );
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        }

        private static void ShouldFailAs(TokenVerification result, TokenVerificationFailure failure)
        {
            result.Failure.Should().Be(failure);
            result.Token.Should().BeNull();
            result.Detail.Should().NotBeEmpty();
        }

        [Test]
        public void It_classifies_an_expired_token_as_expired() =>
            ShouldFailAs(_expired, TokenVerificationFailure.Expired);

        [Test]
        public void It_classifies_a_wrong_issuer_as_an_issuer_or_audience_failure() =>
            ShouldFailAs(_wrongIssuer, TokenVerificationFailure.UntrustedIssuerOrAudience);

        [Test]
        public void It_classifies_a_wrong_audience_as_an_issuer_or_audience_failure() =>
            ShouldFailAs(_wrongAudience, TokenVerificationFailure.UntrustedIssuerOrAudience);

        [Test]
        public void It_rejects_an_unsigned_token_as_untrusted() =>
            ShouldFailAs(_unsigned, TokenVerificationFailure.Untrusted);

        [Test]
        public void It_rejects_an_unknown_kid_as_untrusted() =>
            ShouldFailAs(_unknownKid, TokenVerificationFailure.Untrusted);

        [Test]
        public void It_rejects_a_missing_kid_as_untrusted() =>
            ShouldFailAs(_missingKid, TokenVerificationFailure.Untrusted);

        [Test]
        public void It_rejects_a_signature_from_another_key_as_untrusted() =>
            ShouldFailAs(_otherKey, TokenVerificationFailure.Untrusted);
    }
}
