// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class OpenIddictKeyCryptoTests : DatabaseTest
{
    [TestFixture]
    public class Given_An_Encrypted_Signing_Key : OpenIddictKeyCryptoTests
    {
        private const string EncryptionKey = "IntegrationTestIdentityEncryptionKey!";
        private OpenIddictDataRepository _repository = null!;
        private string _privateKeyBase64 = null!;
        private byte[] _publicKey = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            _privateKeyBase64 = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(1200)
            );
            _publicKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(270);

            // The private key cleartext is cast to the character type the read path expects back,
            // while the encryption passphrase parameter keeps its default string type on both sides.
            await Connection!.ExecuteAsync(
                """
                INSERT INTO dmscs.OpenIddictKey (KeyId, PublicKey, PrivateKey, IsActive)
                VALUES (@KeyId, @PublicKey, ENCRYPTBYPASSPHRASE(@EncryptionKey, CAST(@PrivateKey AS VARCHAR(8000))), 1);
                """,
                new
                {
                    KeyId = "integration-test-key",
                    PublicKey = _publicKey,
                    EncryptionKey,
                    PrivateKey = _privateKeyBase64,
                }
            );
        }

        [Test]
        public async Task It_decrypts_the_private_key_with_the_identity_encryption_key()
        {
            var key = await _repository.GetActivePrivateKeyInternalAsync(EncryptionKey);

            key.Should().NotBeNull();
            key!.Value.PrivateKey.Should().Be(_privateKeyBase64);
            key.Value.KeyId.Should().Be("integration-test-key");
        }

        [Test]
        public async Task It_returns_null_for_a_wrong_passphrase()
        {
            var key = await _repository.GetActivePrivateKeyInternalAsync("NotTheRightPassphrase!");

            key.Should().BeNull();
        }

        [Test]
        public async Task It_returns_the_active_public_key()
        {
            var keys = (await _repository.GetActivePublicKeysInternalAsync()).ToList();

            keys.Should().ContainSingle();
            keys[0].KeyId.Should().Be("integration-test-key");
            keys[0].PublicKey.Should().BeEquivalentTo(_publicKey);
        }
    }
}
