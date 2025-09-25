// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class ConnectionStringEncryptionServiceTests
{
    private ConnectionStringEncryptionService _service = null!;
    private readonly IOptions<DatabaseOptions> _databaseOptions = Options.Create(
        new DatabaseOptions
        {
            DatabaseConnection = "Server=test;",
            EncryptionKey = "TestEncryptionKey123456789012345678901234567890",
        }
    );

    [SetUp]
    public void Setup()
    {
        _service = new ConnectionStringEncryptionService(_databaseOptions);
    }

    [TestFixture]
    public class Given_valid_connection_string : ConnectionStringEncryptionServiceTests
    {
        private const string TestConnectionString =
            "Server=localhost;Database=TestDb;User Id=user;Password=pass;";

        [Test]
        public void It_should_encrypt_and_decrypt_successfully()
        {
            var encrypted = _service.Encrypt(TestConnectionString);
            encrypted.Should().NotBeNull();
            encrypted!.Length.Should().BeGreaterThan(0);

            var decrypted = _service.Decrypt(encrypted);
            decrypted.Should().Be(TestConnectionString);
        }

        [Test]
        public void It_should_produce_different_encrypted_values_for_same_input()
        {
            var encrypted1 = _service.Encrypt(TestConnectionString);
            var encrypted2 = _service.Encrypt(TestConnectionString);

            encrypted1.Should().NotBeNull();
            encrypted2.Should().NotBeNull();
            encrypted1.Should().NotBeEquivalentTo(encrypted2);

            _service.Decrypt(encrypted1).Should().Be(TestConnectionString);
            _service.Decrypt(encrypted2).Should().Be(TestConnectionString);
        }
    }

    [TestFixture]
    public class Given_null_connection_string : ConnectionStringEncryptionServiceTests
    {
        [Test]
        public void It_should_return_null_for_encryption()
        {
            var result = _service.Encrypt(null);
            result.Should().BeNull();
        }

        [Test]
        public void It_should_return_null_for_decryption()
        {
            var result = _service.Decrypt(null);
            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_empty_connection_string : ConnectionStringEncryptionServiceTests
    {
        [Test]
        public void It_should_return_null_for_encryption()
        {
            var result = _service.Encrypt("");
            result.Should().BeNull();
        }

        [Test]
        public void It_should_return_null_for_decryption()
        {
            var result = _service.Decrypt([]);
            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_round_trip_encryption : ConnectionStringEncryptionServiceTests
    {
        [TestCase("Simple")]
        [TestCase("Server=localhost;Database=TestDb;")]
        [TestCase("Server=localhost;Database=TestDb;User Id=user;Password=complex$Pass@word123!;")]
        [TestCase(
            "Data Source=server;Initial Catalog=database;Integrated Security=True;MultipleActiveResultSets=True;"
        )]
        public void It_should_correctly_round_trip_various_connection_strings(string connectionString)
        {
            var encrypted = _service.Encrypt(connectionString);
            var decrypted = _service.Decrypt(encrypted);

            decrypted.Should().Be(connectionString);
        }
    }
}
