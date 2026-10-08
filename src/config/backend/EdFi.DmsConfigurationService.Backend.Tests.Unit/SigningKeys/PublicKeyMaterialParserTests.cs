// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class PublicKeyMaterialParserTests
{
    private static PublicKeyFormat Detect(byte[] keyData) =>
        PublicKeyMaterialParser.DetectFormat(keyData, NullLogger.Instance);

    [TestFixture]
    public class Given_the_supported_encodings_of_one_key
    {
        private RSAParameters _expected;
        private byte[] _spki = null!;
        private byte[] _pkcs1 = null!;
        private byte[] _base64Spki = null!;

        [SetUp]
        public void Setup()
        {
            using RSA rsa = RSA.Create(2048);
            _expected = rsa.ExportParameters(false);
            _spki = rsa.ExportSubjectPublicKeyInfo();
            _pkcs1 = rsa.ExportRSAPublicKey();
            _base64Spki = Encoding.UTF8.GetBytes(Convert.ToBase64String(_spki));
        }

        [Test]
        public void It_detects_subject_public_key_info() =>
            Detect(_spki).Should().Be(PublicKeyFormat.SubjectPublicKeyInfo);

        [Test]
        public void It_detects_pkcs1() => Detect(_pkcs1).Should().Be(PublicKeyFormat.Pkcs1);

        [Test]
        public void It_detects_base64_encoded_subject_public_key_info() =>
            Detect(_base64Spki).Should().Be(PublicKeyFormat.Base64Encoded);

        [Test]
        public void It_imports_subject_public_key_info() =>
            PublicKeyMaterialParser
                .ImportPublicParameters(_spki, PublicKeyFormat.SubjectPublicKeyInfo)
                .Modulus.Should()
                .Equal(_expected.Modulus);

        [Test]
        public void It_imports_pkcs1() =>
            PublicKeyMaterialParser
                .ImportPublicParameters(_pkcs1, PublicKeyFormat.Pkcs1)
                .Modulus.Should()
                .Equal(_expected.Modulus);

        [Test]
        public void It_imports_base64_encoded_subject_public_key_info() =>
            PublicKeyMaterialParser
                .ImportPublicParameters(_base64Spki, PublicKeyFormat.Base64Encoded)
                .Modulus.Should()
                .Equal(_expected.Modulus);

        [Test]
        public void It_keeps_the_exponent() =>
            PublicKeyMaterialParser
                .ImportPublicParameters(_pkcs1, PublicKeyFormat.Pkcs1)
                .Exponent.Should()
                .Equal(_expected.Exponent);

        [Test]
        public void It_returns_public_parameters_only() =>
            PublicKeyMaterialParser
                .ImportPublicParameters(_spki, PublicKeyFormat.SubjectPublicKeyInfo)
                .D.Should()
                .BeNull();
    }

    [TestFixture]
    public class Given_data_in_no_supported_encoding
    {
        [Test]
        public void It_detects_unknown_for_arbitrary_bytes() =>
            Detect([1, 2, 3]).Should().Be(PublicKeyFormat.Unknown);

        // The Base64 path only ever accepted SubjectPublicKeyInfo inside the string; moving the parser keeps that.
        [Test]
        public void It_detects_unknown_for_base64_encoded_pkcs1()
        {
            using RSA rsa = RSA.Create(2048);
            byte[] base64Pkcs1 = Encoding.UTF8.GetBytes(Convert.ToBase64String(rsa.ExportRSAPublicKey()));

            Detect(base64Pkcs1).Should().Be(PublicKeyFormat.Unknown);
        }

        [Test]
        public void It_refuses_to_import_the_unknown_format()
        {
            Action import = () =>
                PublicKeyMaterialParser.ImportPublicParameters([1, 2, 3], PublicKeyFormat.Unknown);

            import.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("format");
        }

        [Test]
        public void It_throws_when_the_data_does_not_match_the_given_format()
        {
            Action import = () =>
                PublicKeyMaterialParser.ImportPublicParameters(
                    [1, 2, 3],
                    PublicKeyFormat.SubjectPublicKeyInfo
                );

            import.Should().Throw<CryptographicException>();
        }
    }
}
