// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeyEntryTests
{
    private static RSAParameters PublicParameters(out RSAParameters privateParameters)
    {
        using RSA rsa = RSA.Create(2048);
        privateParameters = rsa.ExportParameters(true);
        return rsa.ExportParameters(false);
    }

    [TestFixture]
    public class Given_public_parameters
    {
        private RSAParameters _source;
        private byte[] _originalModulus = null!;
        private SigningKeyEntry _entry = null!;

        [SetUp]
        public void Act()
        {
            _source = PublicParameters(out _);
            _originalModulus = (byte[])_source.Modulus!.Clone();
            _entry = SigningKeyEntry.FromRsaPublicParameters("key-1", _source);

            // The caller keeps its arrays; changing them afterwards must not reach the entry.
            _source.Modulus![0] ^= 0xFF;
        }

        [Test]
        public void It_keeps_the_key_id() => _entry.KeyId.Should().Be("key-1");

        [Test]
        public void It_gives_the_validation_key_the_key_id() =>
            _entry.CreateSecurityKey().KeyId.Should().Be("key-1");

        [Test]
        public void It_is_unaffected_by_later_changes_to_the_input() =>
            _entry.PublicParameters.Modulus.Should().Equal(_originalModulus);

        [Test]
        public void It_builds_the_validation_key_from_the_original_modulus() =>
            ((RsaSecurityKey)_entry.CreateSecurityKey()).Parameters.Modulus.Should().Equal(_originalModulus);

        [Test]
        public void It_returns_a_copy_of_the_parameters()
        {
            RSAParameters returned = _entry.PublicParameters;
            returned.Modulus![0] ^= 0xFF;

            _entry.PublicParameters.Modulus.Should().Equal(_originalModulus);
        }

        [Test]
        public void It_creates_a_new_validation_key_each_time() =>
            _entry.CreateSecurityKey().Should().NotBeSameAs(_entry.CreateSecurityKey());

        [Test]
        public void It_exposes_no_private_material()
        {
            RSAParameters returned = _entry.PublicParameters;

            new object?[] { returned.D, returned.P, returned.Q, returned.DP, returned.DQ, returned.InverseQ }
                .Should()
                .OnlyContain(value => value == null);
        }
    }

    [TestFixture]
    public class Given_a_consumer_mutates_a_validation_key
    {
        private byte[] _originalModulus = null!;
        private byte[] _originalExponent = null!;
        private SigningKeyEntry _entry = null!;

        [SetUp]
        public void Act()
        {
            RSAParameters source = PublicParameters(out _);
            _originalModulus = (byte[])source.Modulus!.Clone();
            _originalExponent = (byte[])source.Exponent!.Clone();
            _entry = SigningKeyEntry.FromRsaPublicParameters("key-1", source);

            // SecurityKey.KeyId is settable, and RsaSecurityKey.Parameters returns a struct whose arrays are the key's
            // own: both are ways a consumer could corrupt a shared key.
            var handedOut = (RsaSecurityKey)_entry.CreateSecurityKey();
            handedOut.KeyId = "attacker";
            handedOut.Parameters.Modulus![0] ^= 0xFF;
            handedOut.Parameters.Exponent![0] ^= 0xFF;
        }

        [Test]
        public void It_keeps_the_entry_key_id() => _entry.KeyId.Should().Be("key-1");

        [Test]
        public void It_gives_later_validation_keys_the_original_key_id() =>
            _entry.CreateSecurityKey().KeyId.Should().Be("key-1");

        [Test]
        public void It_gives_later_validation_keys_the_original_modulus() =>
            ((RsaSecurityKey)_entry.CreateSecurityKey()).Parameters.Modulus.Should().Equal(_originalModulus);

        [Test]
        public void It_gives_later_validation_keys_the_original_exponent() =>
            ((RsaSecurityKey)_entry.CreateSecurityKey())
                .Parameters.Exponent.Should()
                .Equal(_originalExponent);

        [Test]
        public void It_keeps_the_jwks_projection_unchanged()
        {
            RSAParameters projected = _entry.PublicParameters;

            projected.Modulus.Should().Equal(_originalModulus);
            projected.Exponent.Should().Equal(_originalExponent);
        }
    }

    [TestFixture]
    public class Given_private_parameters
    {
        [Test]
        public void It_refuses_them()
        {
            PublicParameters(out RSAParameters privateParameters);

            Action create = () => SigningKeyEntry.FromRsaPublicParameters("key-1", privateParameters);

            create
                .Should()
                .Throw<ArgumentException>()
                .WithMessage("A signing key entry must not carry private-key material.*");
        }
    }

    [TestFixture]
    public class Given_an_empty_key_id
    {
        [Test]
        public void It_refuses_it()
        {
            RSAParameters parameters = PublicParameters(out _);

            Action create = () => SigningKeyEntry.FromRsaPublicParameters(" ", parameters);

            create.Should().Throw<ArgumentException>().WithParameterName("keyId");
        }
    }

    [TestFixture]
    public class Given_parameters_without_a_modulus
    {
        [Test]
        public void It_refuses_them()
        {
            RSAParameters parameters = PublicParameters(out _);
            parameters.Modulus = [];

            Action create = () => SigningKeyEntry.FromRsaPublicParameters("key-1", parameters);

            create.Should().Throw<ArgumentException>().WithParameterName("parameters");
        }
    }
}
