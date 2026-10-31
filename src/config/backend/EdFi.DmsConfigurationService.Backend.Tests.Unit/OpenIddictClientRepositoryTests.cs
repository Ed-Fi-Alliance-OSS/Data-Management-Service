// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class OpenIddictClientRepositoryTests
{
    private ILogger<OpenIddictClientRepository> _logger = null!;
    private IClientSecretHasher _secretHasher = null!;
    private IOpenIddictDataRepository _dataRepository = null!;
    private IOptions<ClientSecretValidationOptions> _clientSecretValidationOptionsAccessor = null!;
    private OpenIddictClientRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<OpenIddictClientRepository>>();
        _secretHasher = A.Fake<IClientSecretHasher>();
        _dataRepository = A.Fake<IOpenIddictDataRepository>();
        _clientSecretValidationOptionsAccessor = Options.Create(new ClientSecretValidationOptions());
        var connection = A.Fake<IDbConnection>();
        var transaction = A.Fake<IDbTransaction>();

        A.CallTo(() => _dataRepository.CreateConnectionAsync()).Returns(connection);
        A.CallTo(() => _dataRepository.BeginTransactionAsync(connection)).Returns(transaction);
        A.CallTo(() => _secretHasher.HashSecretAsync(A<string>.Ignored))
            .Returns(Task.FromResult("hashed_secret"));

        _repository = new OpenIddictClientRepository(
            _logger,
            _secretHasher,
            _dataRepository,
            _clientSecretValidationOptionsAccessor
        );
    }

    [TestFixture]
    public class Given_CreateClientAsync_With_DataStoreIds : OpenIddictClientRepositoryTests
    {
        [Test]
        public async Task It_should_include_dataStoreIds_claim_when_provided()
        {
            // Arrange
            var dataStoreIds = new int[] { 3, 1, 2 }; // Unsorted to test sorting

            A.CallTo(() =>
                    _dataRepository.FindRoleIdByNameAsync(
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(Guid.NewGuid());

            string? capturedProtocolMappers = null;
            A.CallTo(() =>
                    _dataRepository.InsertApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string>._,
                        A<string>._,
                        A<string[]>._,
                        A<string[]>._,
                        A<string>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(7))
                .Returns(OpenIddictApplicationInsertOutcome.Inserted);

            // Act
            var result = await _repository.CreateClientAsync(
                "test-client",
                "test-secret",
                "test-role",
                "Test Client",
                "test-scope",
                "uri://test",
                "100,200",
                dataStoreIds
            );

            // Assert
            result.Should().BeOfType<ClientCreateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dataStoreIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dataStoreIds"
            );

            dataStoreIdsClaim.Should().NotBeNull("dataStoreIds claim should be present");
            dataStoreIdsClaim!["claim.value"].Should().Be("1,2,3", "IDs should be sorted");
        }

        [Test]
        public async Task It_should_not_include_dataStoreIds_claim_when_null()
        {
            // Arrange
            A.CallTo(() =>
                    _dataRepository.FindRoleIdByNameAsync(
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(Guid.NewGuid());

            string? capturedProtocolMappers = null;
            A.CallTo(() =>
                    _dataRepository.InsertApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string>._,
                        A<string>._,
                        A<string[]>._,
                        A<string[]>._,
                        A<string>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(7))
                .Returns(OpenIddictApplicationInsertOutcome.Inserted);

            // Act
            var result = await _repository.CreateClientAsync(
                "test-client",
                "test-secret",
                "test-role",
                "Test Client",
                "test-scope",
                "uri://test",
                "100,200",
                null // No dataStoreIds
            );

            // Assert
            result.Should().BeOfType<ClientCreateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dataStoreIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dataStoreIds"
            );

            dataStoreIdsClaim.Should().BeNull("dataStoreIds claim should not be present when null");
        }

        [Test]
        public async Task It_should_handle_empty_dataStoreIds_array()
        {
            // Arrange
            var dataStoreIds = Array.Empty<int>();

            A.CallTo(() =>
                    _dataRepository.FindRoleIdByNameAsync(
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(Guid.NewGuid());

            string? capturedProtocolMappers = null;
            A.CallTo(() =>
                    _dataRepository.InsertApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string>._,
                        A<string>._,
                        A<string[]>._,
                        A<string[]>._,
                        A<string>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(7))
                .Returns(OpenIddictApplicationInsertOutcome.Inserted);

            // Act
            var result = await _repository.CreateClientAsync(
                "test-client",
                "test-secret",
                "test-role",
                "Test Client",
                "test-scope",
                "uri://test",
                "100,200",
                dataStoreIds
            );

            // Assert
            result.Should().BeOfType<ClientCreateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dataStoreIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dataStoreIds"
            );

            dataStoreIdsClaim.Should().NotBeNull("dataStoreIds claim should be present even when empty");
            dataStoreIdsClaim!["claim.value"].Should().Be("", "Empty array should result in empty string");
        }
    }

    [TestFixture]
    public class Given_UpdateClientAsync_With_DataStoreIds : OpenIddictClientRepositoryTests
    {
        [Test]
        public async Task It_should_merge_dataStoreIds_claim_when_provided()
        {
            // Arrange
            var clientUuid = Guid.NewGuid().ToString();
            var dataStoreIds = new int[] { 5, 3, 4 };

            var existingProtocolMappers = JsonSerializer.Serialize(
                new List<Dictionary<string, string>>
                {
                    new() { { "claim.name", "namespacePrefixes" }, { "claim.value", "uri://test" } },
                    new() { { "claim.name", "educationOrganizationIds" }, { "claim.value", "100" } },
                }
            );

            A.CallTo(() =>
                    _dataRepository.GetApplicationByIdAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(
                    new ApplicationInfo
                    {
                        Id = Guid.Parse(clientUuid),
                        ClientId = "test-client",
                        DisplayName = "Test Client",
                        ProtocolMappers = existingProtocolMappers,
                    }
                );

            string? capturedProtocolMappers = null;
            A.CallTo(() =>
                    _dataRepository.UpdateApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string[]>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(3))
                .Returns(1);

            // Act
            var result = await _repository.UpdateClientAsync(
                clientUuid,
                "Updated Client",
                "test-scope",
                "200,300",
                dataStoreIds
            );

            // Assert
            result.Should().BeOfType<ClientUpdateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dataStoreIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dataStoreIds"
            );

            dataStoreIdsClaim.Should().NotBeNull("dataStoreIds claim should be present");
            dataStoreIdsClaim!["claim.value"].Should().Be("3,4,5", "IDs should be sorted");
        }

        [Test]
        public async Task It_should_update_existing_dataStoreIds_claim()
        {
            // Arrange
            var clientUuid = Guid.NewGuid().ToString();
            var newDataStoreIds = new int[] { 10, 20 };

            var existingProtocolMappers = JsonSerializer.Serialize(
                new List<Dictionary<string, string>>
                {
                    new() { { "claim.name", "namespacePrefixes" }, { "claim.value", "uri://test" } },
                    new() { { "claim.name", "educationOrganizationIds" }, { "claim.value", "100" } },
                    new() { { "claim.name", "dataStoreIds" }, { "claim.value", "1,2,3" } }, // Old value
                }
            );

            A.CallTo(() =>
                    _dataRepository.GetApplicationByIdAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(
                    new ApplicationInfo
                    {
                        Id = Guid.Parse(clientUuid),
                        ClientId = "test-client",
                        DisplayName = "Test Client",
                        ProtocolMappers = existingProtocolMappers,
                    }
                );

            string? capturedProtocolMappers = null;
            A.CallTo(() =>
                    _dataRepository.UpdateApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string[]>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(3))
                .Returns(1);

            // Act
            var result = await _repository.UpdateClientAsync(
                clientUuid,
                "Updated Client",
                "test-scope",
                "200,300",
                newDataStoreIds
            );

            // Assert
            result.Should().BeOfType<ClientUpdateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dataStoreIdsClaims = protocolMappers!
                .Where(m => m.ContainsKey("claim.name") && m["claim.name"] == "dataStoreIds")
                .ToList();

            dataStoreIdsClaims.Should().HaveCount(1, "Should have only one dataStoreIds claim");
            dataStoreIdsClaims[0]["claim.value"].Should().Be("10,20", "Should have new sorted IDs");
        }

        [Test]
        public async Task It_should_not_modify_dataStoreIds_when_null()
        {
            // Arrange
            var clientUuid = Guid.NewGuid().ToString();

            var existingProtocolMappers = JsonSerializer.Serialize(
                new List<Dictionary<string, string>>
                {
                    new() { { "claim.name", "namespacePrefixes" }, { "claim.value", "uri://test" } },
                    new() { { "claim.name", "educationOrganizationIds" }, { "claim.value", "100" } },
                    new() { { "claim.name", "dataStoreIds" }, { "claim.value", "1,2,3" } },
                }
            );

            A.CallTo(() =>
                    _dataRepository.GetApplicationByIdAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(
                    new ApplicationInfo
                    {
                        Id = Guid.Parse(clientUuid),
                        ClientId = "test-client",
                        DisplayName = "Test Client",
                        ProtocolMappers = existingProtocolMappers,
                    }
                );

            string? capturedProtocolMappers = null;
            A.CallTo(() =>
                    _dataRepository.UpdateApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string[]>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(3))
                .Returns(1);

            // Act
            var result = await _repository.UpdateClientAsync(
                clientUuid,
                "Updated Client",
                "test-scope",
                "200,300",
                null // Don't update dataStoreIds
            );

            // Assert
            result.Should().BeOfType<ClientUpdateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dataStoreIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dataStoreIds"
            );

            // When null is passed, the existing dataStoreIds should be preserved
            dataStoreIdsClaim.Should().NotBeNull("Existing dataStoreIds claim should be preserved");
            dataStoreIdsClaim!["claim.value"].Should().Be("1,2,3", "Original value should be unchanged");
        }
    }

    [TestFixture]
    public class Given_ResetCredentialsAsync : OpenIddictClientRepositoryTests
    {
        [SetUp]
        public void ResetSetup()
        {
            _clientSecretValidationOptionsAccessor = Options.Create(
                new ClientSecretValidationOptions { MinimumLength = 40, MaximumLength = 128 }
            );

            _repository = new OpenIddictClientRepository(
                _logger,
                _secretHasher,
                _dataRepository,
                _clientSecretValidationOptionsAccessor
            );
        }

        [Test]
        public async Task It_should_generate_a_secret_using_the_configured_minimum_length()
        {
            // Arrange
            A.CallTo(() =>
                    _dataRepository.UpdateClientSecretAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(1);

            // Act
            var result = await _repository.ResetCredentialsAsync(Guid.NewGuid().ToString());

            // Assert
            result.Should().BeOfType<ClientResetResult.Success>();
            var success = (ClientResetResult.Success)result;
            success.ClientSecret.Should().HaveLength(40);
            Regex
                .IsMatch(
                    success.ClientSecret,
                    ClientSecretValidation.BuildComplexityPattern(
                        _clientSecretValidationOptionsAccessor.Value
                    )
                )
                .Should()
                .BeTrue();
            A.CallTo(() => _secretHasher.HashSecretAsync(success.ClientSecret)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_should_generate_a_secret_free_of_transport_unsafe_characters()
        {
            // Arrange
            A.CallTo(() =>
                    _dataRepository.UpdateClientSecretAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(1);

            // Act
            var result = await _repository.ResetCredentialsAsync(Guid.NewGuid().ToString());

            // Assert
            result.Should().BeOfType<ClientResetResult.Success>();
            var success = (ClientResetResult.Success)result;
            success.ClientSecret.Should().NotContainAny("+", "%", "=", "&", " ");
        }
    }

    /// <summary>
    /// Shared arrangement for the credential reset (DMS-1493 §6.2). The reset writes the new hash
    /// and revokes the client's tokens in one transaction, so every fixture keeps the connection and
    /// the transaction it hands out and checks that both calls ran on them.
    /// </summary>
    public abstract class ResetCredentialsTestBase : OpenIddictClientRepositoryTests
    {
        protected const string HashedNewSecret = "hashed_secret";
        protected const string RevocationLog = "outstanding tokens for client";

        protected Guid _clientUuid;
        protected IDbConnection _connection = null!;
        protected IDbTransaction _transaction = null!;
        protected ClientResetResult _result = null!;

        [SetUp]
        public void SetUpResetDefaults()
        {
            _clientUuid = Guid.NewGuid();
            _connection = A.Fake<IDbConnection>();
            _transaction = A.Fake<IDbTransaction>();
            A.CallTo(() => _dataRepository.CreateConnectionAsync()).Returns(_connection);
            A.CallTo(() => _dataRepository.BeginTransactionAsync(_connection)).Returns(_transaction);
        }

        [TearDown]
        public void DisposeResetFakes()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        protected void ArrangeSecretUpdate(int rows) =>
            A.CallTo(() =>
                    _dataRepository.UpdateClientSecretAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(rows);

        protected async Task ActResetAsync(string? clientUuid = null) =>
            _result = await _repository.ResetCredentialsAsync(clientUuid ?? _clientUuid.ToString());

        protected void AssertNoRevocation() =>
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();

        protected void AssertNoRevocationLogged() => _logger.VerifyNoLog(LogLevel.Information, RevocationLog);
    }

    /// <summary>
    /// The reset writes the new hash first, which takes the application row lock that token stores
    /// wait on, then revokes the client's tokens on the same connection and transaction, and commits
    /// once before returning the new secret. Fails if the revocation is missing, runs outside the
    /// reset's transaction, or runs before the secret write.
    /// </summary>
    [TestFixture]
    public class Given_ResetCredentialsAsync_For_An_Existing_Client : ResetCredentialsTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeSecretUpdate(1);
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(3);

            // The canonical form of the identifier differs from what is passed in, so the log shows
            // whether the reset logs the parsed identifier rather than the caller's string.
            await ActResetAsync(_clientUuid.ToString("B").ToUpperInvariant());
        }

        [Test]
        public void It_returns_success_carrying_the_secret_it_hashed()
        {
            _result.Should().BeOfType<ClientResetResult.Success>();
            var newSecret = ((ClientResetResult.Success)_result).ClientSecret;
            A.CallTo(() => _secretHasher.HashSecretAsync(newSecret)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_writes_the_new_hash_inside_the_reset_transaction() =>
            A.CallTo(() =>
                    _dataRepository.UpdateClientSecretAsync(
                        _clientUuid,
                        HashedNewSecret,
                        _connection,
                        _transaction
                    )
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_revokes_the_client_tokens_inside_the_same_transaction() =>
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(_clientUuid, _connection, _transaction)
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_hashes_the_secret_before_it_opens_the_transaction() =>
            A.CallTo(() => _secretHasher.HashSecretAsync(A<string>._))
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() => _dataRepository.BeginTransactionAsync(_connection))
                        .MustHaveHappenedOnceExactly()
                );

        [Test]
        public void It_writes_the_secret_then_revokes_then_commits() =>
            A.CallTo(() =>
                    _dataRepository.UpdateClientSecretAsync(
                        _clientUuid,
                        HashedNewSecret,
                        _connection,
                        _transaction
                    )
                )
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() =>
                            _dataRepository.RevokeTokensForApplicationAsync(
                                _clientUuid,
                                _connection,
                                _transaction
                            )
                        )
                        .MustHaveHappenedOnceExactly()
                )
                .Then(A.CallTo(() => _transaction.Commit()).MustHaveHappenedOnceExactly());

        [Test]
        public void It_does_not_roll_back() => A.CallTo(() => _transaction.Rollback()).MustNotHaveHappened();

        [Test]
        public void It_logs_the_revocation_count_with_the_normalized_client_uuid() =>
            _logger
                .LoggedEntries()
                .Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message == $"Revoked 3 outstanding tokens for client {_clientUuid}"
                );

        [Test]
        public void It_logs_no_secret()
        {
            var newSecret = ((ClientResetResult.Success)_result).ClientSecret;
            _logger
                .LoggedEntryTexts()
                .Should()
                .NotContain(text => text.Contains(newSecret) || text.Contains(HashedNewSecret));
        }
    }

    /// <summary>
    /// No application row matched, so nothing changed: the reset rolls back without revoking and
    /// answers not found. Fails if a missing client still revokes or commits.
    /// </summary>
    [TestFixture]
    public class Given_ResetCredentialsAsync_For_A_Missing_Client : ResetCredentialsTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeSecretUpdate(0);

            await ActResetAsync();
        }

        [Test]
        public void It_returns_failure_client_not_found() =>
            _result.Should().BeOfType<ClientResetResult.FailureClientNotFound>();

        [Test]
        public void It_revokes_nothing() => AssertNoRevocation();

        [Test]
        public void It_rolls_back_without_committing()
        {
            A.CallTo(() => _transaction.Rollback()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();
        }

        [Test]
        public void It_logs_no_revocation() => AssertNoRevocationLogged();
    }

    /// <summary>
    /// The revocation fails after the new hash was written in the same transaction. The reset
    /// answers an unknown failure and never commits, so disposing the transaction discards the
    /// secret change together with the revocation. Fails if the secret change is committed before
    /// the revocation succeeds.
    /// </summary>
    [TestFixture]
    public class Given_ResetCredentialsAsync_Whose_Revocation_Throws : ResetCredentialsTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeSecretUpdate(1);
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .ThrowsAsync(new InvalidOperationException("revocation failed"));

            await ActResetAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientResetResult.FailureUnknown>();

        [Test]
        public void It_does_not_commit() => A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();

        [Test]
        public void It_disposes_the_uncommitted_transaction() =>
            A.CallTo(() => _transaction.Dispose()).MustHaveHappened();

        [Test]
        public void It_logs_the_failure_as_an_error() =>
            _logger.VerifyLogError("Failed to reset OpenIddict client credentials");

        [Test]
        public void It_logs_no_revocation() => AssertNoRevocationLogged();
    }

    /// <summary>
    /// Shared arrangement for the client deletion (DMS-1493 §6.3). The deletion removes the
    /// application row and revokes the client's tokens in one transaction, so every fixture keeps
    /// the connection and the transaction it hands out and checks that both calls ran on them.
    /// </summary>
    public abstract class DeleteClientTestBase : OpenIddictClientRepositoryTests
    {
        protected const string RevocationLog = "outstanding tokens for client";

        protected Guid _clientUuid;
        protected IDbConnection _connection = null!;
        protected IDbTransaction _transaction = null!;
        protected ClientDeleteResult _result = null!;

        [SetUp]
        public void SetUpDeleteDefaults()
        {
            _clientUuid = Guid.NewGuid();
            _connection = A.Fake<IDbConnection>();
            _transaction = A.Fake<IDbTransaction>();
            A.CallTo(() => _dataRepository.CreateConnectionAsync()).Returns(_connection);
            A.CallTo(() => _dataRepository.BeginTransactionAsync(_connection)).Returns(_transaction);
        }

        [TearDown]
        public void DisposeDeleteFakes()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        protected void ArrangeDelete(int rows) =>
            A.CallTo(() =>
                    _dataRepository.DeleteApplicationByIdAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(rows);

        protected void ArrangeRevocation(int revokedCount) =>
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(revokedCount);

        // The canonical form of the identifier differs from what is passed in, so the log shows
        // whether the deletion logs the parsed identifier rather than the caller's string.
        protected async Task ActDeleteAsync() =>
            _result = await _repository.DeleteClientAsync(_clientUuid.ToString("B").ToUpperInvariant());

        protected void AssertDeleteThenRevokeThenCommit() =>
            A.CallTo(() => _dataRepository.DeleteApplicationByIdAsync(_clientUuid, _connection, _transaction))
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() =>
                            _dataRepository.RevokeTokensForApplicationAsync(
                                _clientUuid,
                                _connection,
                                _transaction
                            )
                        )
                        .MustHaveHappenedOnceExactly()
                )
                .Then(A.CallTo(() => _transaction.Commit()).MustHaveHappenedOnceExactly());

        protected void AssertRevocationLogged(int revokedCount) =>
            _logger
                .LoggedEntries()
                .Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message == $"Revoked {revokedCount} outstanding tokens for client {_clientUuid}"
                );
    }

    /// <summary>
    /// The deletion removes the application row first, which takes the application row lock that
    /// token stores wait on, then revokes the client's tokens on the same connection and transaction,
    /// and commits once before answering success. Fails if the revocation is missing, runs outside
    /// the deletion's transaction, or runs before the delete.
    /// </summary>
    [TestFixture]
    public class Given_DeleteClientAsync_For_An_Existing_Client : DeleteClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDelete(1);
            ArrangeRevocation(3);

            await ActDeleteAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientDeleteResult.Success>();

        [Test]
        public void It_deletes_the_application_inside_the_deletion_transaction() =>
            A.CallTo(() => _dataRepository.DeleteApplicationByIdAsync(_clientUuid, _connection, _transaction))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_revokes_the_client_tokens_inside_the_same_transaction() =>
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(_clientUuid, _connection, _transaction)
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_deletes_then_revokes_then_commits() => AssertDeleteThenRevokeThenCommit();

        [Test]
        public void It_does_not_roll_back() => A.CallTo(() => _transaction.Rollback()).MustNotHaveHappened();

        [Test]
        public void It_logs_the_revocation_count_with_the_normalized_client_uuid() =>
            AssertRevocationLogged(3);
    }

    /// <summary>
    /// No application row matched, but tokens stored for the identifier remain, for example from a
    /// deletion before revocation existed. The deletion still revokes them and commits before it
    /// answers not found (D-6), which the module treats as an already completed deletion. Fails if a
    /// missing row skips the revocation, rolls it back, or answers anything but not found.
    /// </summary>
    [TestFixture]
    public class Given_DeleteClientAsync_For_A_Missing_Client : DeleteClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDelete(0);
            ArrangeRevocation(2);

            await ActDeleteAsync();
        }

        [Test]
        public void It_returns_failure_client_not_found() =>
            _result.Should().BeOfType<ClientDeleteResult.FailureClientNotFound>();

        [Test]
        public void It_revokes_the_orphaned_tokens_inside_the_same_transaction() =>
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(_clientUuid, _connection, _transaction)
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_deletes_then_revokes_then_commits() => AssertDeleteThenRevokeThenCommit();

        [Test]
        public void It_does_not_roll_back() => A.CallTo(() => _transaction.Rollback()).MustNotHaveHappened();

        [Test]
        public void It_logs_the_revocation_count_with_the_normalized_client_uuid() =>
            AssertRevocationLogged(2);
    }

    /// <summary>
    /// The revocation fails after the application row was deleted in the same transaction. The
    /// deletion answers an unknown failure and never commits, so disposing the transaction restores
    /// the application row together with the revocation. Fails if the deletion is committed before
    /// the revocation succeeds.
    /// </summary>
    [TestFixture]
    public class Given_DeleteClientAsync_Whose_Revocation_Throws : DeleteClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDelete(1);
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .ThrowsAsync(new InvalidOperationException("revocation failed"));

            await ActDeleteAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientDeleteResult.FailureUnknown>();

        [Test]
        public void It_does_not_commit() => A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();

        [Test]
        public void It_disposes_the_uncommitted_transaction() =>
            A.CallTo(() => _transaction.Dispose()).MustHaveHappened();

        [Test]
        public void It_logs_the_failure_as_an_error() =>
            _logger.VerifyLogError("Failed to delete OpenIddict client");

        [Test]
        public void It_logs_no_revocation() => _logger.VerifyNoLog(LogLevel.Information, RevocationLog);
    }

    /// <summary>
    /// Regression cover for the OpenIddict namespace-claim update, which DMS-1356 leaves
    /// unchanged. Its in-place behavior is the contract the Vendor workflow relies on for the
    /// self-contained provider: the stored application row is updated under its existing
    /// identifier and <see cref="ClientUpdateResult.Success"/> carries that same identifier back,
    /// so the guarded UUID synchronization resolves as already applied rather than rewriting the
    /// row. These fixtures fail if the provider is ever changed to recreate the application.
    /// </summary>
    public abstract class NamespaceClaimUpdateTestBase : OpenIddictClientRepositoryTests
    {
        protected const string NewPrefixes = "uri://ed-fi.org,uri://new.org";

        protected Guid _clientUuid;
        protected IDbConnection _connection = null!;
        protected IDbTransaction _transaction = null!;
        protected ClientUpdateResult _result = null!;
        protected string? _capturedProtocolMappers;
        protected Guid _updatedApplicationId;

        [SetUp]
        public void SetUpNamespaceClaimDefaults()
        {
            _clientUuid = Guid.NewGuid();
            _capturedProtocolMappers = null;
            _updatedApplicationId = Guid.Empty;

            _connection = A.Fake<IDbConnection>();
            _transaction = A.Fake<IDbTransaction>();
            A.CallTo(() => _dataRepository.CreateConnectionAsync()).Returns(_connection);
            A.CallTo(() => _dataRepository.BeginTransactionAsync(_connection)).Returns(_transaction);

            StoreProtocolMappers(
                JsonSerializer.Serialize(
                    new List<Dictionary<string, string>>
                    {
                        new() { { "claim.name", "namespacePrefixes" }, { "claim.value", "uri://ed-fi.org" } },
                        new() { { "claim.name", "educationOrganizationIds" }, { "claim.value", "100" } },
                        new() { { "claim.name", "dataStoreIds" }, { "claim.value", "7,8" } },
                    }
                )
            );

            A.CallTo(() =>
                    _dataRepository.UpdateApplicationProtocolMappersAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call =>
                {
                    _updatedApplicationId = call.GetArgument<Guid>(0);
                    _capturedProtocolMappers = call.GetArgument<string>(1);
                })
                .Returns(1);
        }

        [TearDown]
        public void DisposeDatabaseFakes()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        protected void StoreProtocolMappers(string protocolMappers) =>
            A.CallTo(() =>
                    _dataRepository.GetApplicationByIdAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(
                    new ApplicationInfo
                    {
                        Id = _clientUuid,
                        ClientId = "test-client",
                        DisplayName = "Test Client",
                        ProtocolMappers = protocolMappers,
                    }
                );

        protected async Task ActUpdateAsync(
            string? clientUuid = null,
            string namespacePrefixes = NewPrefixes
        ) =>
            _result = await _repository.UpdateClientNamespaceClaimAsync(
                clientUuid ?? _clientUuid.ToString(),
                namespacePrefixes
            );

        protected List<Dictionary<string, string>> AppliedMappers() =>
            JsonSerializer.Deserialize<List<Dictionary<string, string>>>(_capturedProtocolMappers!)!;

        protected static List<Dictionary<string, string>> ClaimsNamed(
            List<Dictionary<string, string>> mappers,
            string claimName
        ) => mappers.FindAll(mapper => mapper.GetValueOrDefault("claim.name") == claimName);

        protected static string? ClaimValue(List<Dictionary<string, string>> mappers, string claimName) =>
            ClaimsNamed(mappers, claimName).FirstOrDefault()?.GetValueOrDefault("claim.value");

        protected void AssertNoRowWasUpdated() =>
            A.CallTo(() =>
                    _dataRepository.UpdateApplicationProtocolMappersAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_For_An_Existing_Client : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act() => await ActUpdateAsync();

        [Test]
        public void It_returns_success_carrying_the_unchanged_client_uuid()
        {
            _result.Should().BeOfType<ClientUpdateResult.Success>();
            ((ClientUpdateResult.Success)_result).ClientUuid.Should().Be(_clientUuid);
        }

        [Test]
        public void It_updates_the_stored_application_in_place() =>
            _updatedApplicationId.Should().Be(_clientUuid);

        [Test]
        public void It_applies_the_requested_namespace_prefixes() =>
            ClaimValue(AppliedMappers(), "namespacePrefixes").Should().Be(NewPrefixes);

        [Test]
        public void It_leaves_exactly_one_namespace_claim() =>
            ClaimsNamed(AppliedMappers(), "namespacePrefixes").Should().ContainSingle();

        [Test]
        public void It_preserves_the_education_organization_and_data_store_claims()
        {
            ClaimValue(AppliedMappers(), "educationOrganizationIds").Should().Be("100");
            ClaimValue(AppliedMappers(), "dataStoreIds").Should().Be("7,8");
        }

        [Test]
        public void It_commits_the_transaction()
        {
            A.CallTo(() => _transaction.Commit()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _transaction.Rollback()).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_Clearing_The_Existing_Claim
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act() => await ActUpdateAsync(namespacePrefixes: "");

        [Test]
        public void It_returns_success_carrying_the_unchanged_client_uuid()
        {
            _result.Should().BeOfType<ClientUpdateResult.Success>();
            ((ClientUpdateResult.Success)_result).ClientUuid.Should().Be(_clientUuid);
            _updatedApplicationId.Should().Be(_clientUuid);
        }

        [Test]
        public void It_keeps_one_empty_namespace_mapper()
        {
            ClaimsNamed(AppliedMappers(), "namespacePrefixes").Should().ContainSingle();
            ClaimValue(AppliedMappers(), "namespacePrefixes").Should().BeEmpty();
        }

        [Test]
        public void It_preserves_the_other_mappers()
        {
            ClaimValue(AppliedMappers(), "educationOrganizationIds").Should().Be("100");
            ClaimValue(AppliedMappers(), "dataStoreIds").Should().Be("7,8");
        }

        [Test]
        public void It_commits_without_rollback()
        {
            A.CallTo(() => _transaction.Commit()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _transaction.Rollback()).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_For_A_Client_Without_The_Claim
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            StoreProtocolMappers(
                JsonSerializer.Serialize(
                    new List<Dictionary<string, string>>
                    {
                        new() { { "claim.name", "educationOrganizationIds" }, { "claim.value", "100" } },
                    }
                )
            );

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_adds_the_namespace_claim()
        {
            Dictionary<string, string> added = ClaimsNamed(AppliedMappers(), "namespacePrefixes")
                .Should()
                .ContainSingle()
                .Subject;
            added["claim.value"].Should().Be(NewPrefixes);
            added["jsonType.label"].Should().Be("String");
        }

        [Test]
        public void It_preserves_the_existing_claim() =>
            ClaimValue(AppliedMappers(), "educationOrganizationIds").Should().Be("100");
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_With_Malformed_Stored_Mappers
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            StoreProtocolMappers("not-valid-json");

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_writes_only_the_namespace_claim()
        {
            AppliedMappers().Should().ContainSingle();
            ClaimValue(AppliedMappers(), "namespacePrefixes").Should().Be(NewPrefixes);
        }
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_For_A_Missing_Application
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _dataRepository.GetApplicationByIdAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(Task.FromResult<ApplicationInfo?>(null));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_not_found() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureNotFound>();

        [Test]
        public void It_updates_no_row() => AssertNoRowWasUpdated();

        [Test]
        public void It_rolls_the_transaction_back()
        {
            A.CallTo(() => _transaction.Rollback()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_Whose_Update_Affects_No_Rows
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _dataRepository.UpdateApplicationProtocolMappersAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(0);

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_not_found() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureNotFound>();

        [Test]
        public void It_rolls_the_transaction_back()
        {
            A.CallTo(() => _transaction.Rollback()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_Whose_Update_Throws : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _dataRepository.UpdateApplicationProtocolMappersAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Throws(new InvalidOperationException("the connection dropped"));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_does_not_commit() => A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_UpdateClientNamespaceClaimAsync_With_An_Invalid_Client_Uuid
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act() => await ActUpdateAsync("not-a-uuid");

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_never_reads_the_application() =>
            A.CallTo(() =>
                    _dataRepository.GetApplicationByIdAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_updates_no_row() => AssertNoRowWasUpdated();
    }

    /// <summary>
    /// Shared arrangement for the provider-create path (DMS-1529 D-5). The role is absent, so the
    /// attempt creates one before the application insert: that is the row a key conflict has to
    /// roll back. Every fixture arranges the application-insert outcome explicitly; nothing here
    /// relies on the enum's default value.
    /// </summary>
    public abstract class CreateClientTestBase : OpenIddictClientRepositoryTests
    {
        protected const string ClientKey = "dms1529-client-key";
        protected const string ClientSecret = "dms1529-plain-secret-value";
        protected const string RoleName = "dms-client";
        protected const string ScopeName = "edfi_admin_api/full_access";
        protected const string DuplicateKeyWarning = "no client was created";

        protected IDbConnection _connection = null!;
        protected IDbTransaction _transaction = null!;
        protected ClientCreateResult _result = null!;

        [SetUp]
        public void SetUpCreateDefaults()
        {
            _connection = A.Fake<IDbConnection>();
            _transaction = A.Fake<IDbTransaction>();
            A.CallTo(() => _dataRepository.CreateConnectionAsync()).Returns(_connection);
            A.CallTo(() => _dataRepository.BeginTransactionAsync(_connection)).Returns(_transaction);
            A.CallTo(() => _dataRepository.FindRoleIdByNameAsync(RoleName, _connection, _transaction))
                .Returns(Task.FromResult<Guid?>(null));
            A.CallTo(() => _dataRepository.FindScopeIdByNameAsync(ScopeName, _connection, _transaction))
                .Returns(Task.FromResult<Guid?>(null));
            ArrangeApplicationInsert(OpenIddictApplicationInsertOutcome.Inserted);
        }

        [TearDown]
        public void DisposeCreateFakes()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        protected void ArrangeApplicationInsert(OpenIddictApplicationInsertOutcome outcome) =>
            A.CallTo(() =>
                    _dataRepository.InsertApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string>._,
                        A<string>._,
                        A<string[]>._,
                        A<string[]>._,
                        A<string>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Returns(outcome);

        protected async Task ActCreateAsync(string clientId = ClientKey) =>
            _result = await _repository.CreateClientAsync(
                clientId,
                ClientSecret,
                RoleName,
                "DMS-1529 Client",
                ScopeName,
                "uri://ed-fi.org",
                "255901",
                [1, 2]
            );

        protected void AssertTransactionRolledBackWithoutCommit()
        {
            A.CallTo(() => _transaction.Rollback()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();
        }

        protected void AssertNothingWrittenAfterTheApplicationInsert()
        {
            A.CallTo(() =>
                    _dataRepository.InsertScopeAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _dataRepository.InsertApplicationScopeAsync(
                        A<Guid>._,
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _dataRepository.InsertClientRoleAsync(
                        A<Guid>._,
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();
        }

        protected void AssertNoDuplicateKeyWarning() =>
            _logger.VerifyNoLog(LogLevel.Warning, DuplicateKeyWarning);

        // NotContain rather than OnlyContain: the success path logs nothing at all, and an empty
        // log must satisfy "no secret was logged".
        protected void AssertNoSecretLogged() =>
            _logger.LoggedEntryTexts().Should().NotContain(text => text.Contains(ClientSecret));
    }

    /// <summary>
    /// The success path with the outcome arranged explicitly. Fails if the outcome check is ever
    /// inverted or the role, scope and client-role writes stop following a free key.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_key_is_free : CreateClientTestBase
    {
        private Guid _insertedApplicationId;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _dataRepository.InsertApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string>._,
                        A<string>._,
                        A<string[]>._,
                        A<string[]>._,
                        A<string>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Invokes(call => _insertedApplicationId = call.GetArgument<Guid>(0))
                .Returns(OpenIddictApplicationInsertOutcome.Inserted);

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_success_carrying_the_inserted_identifier()
        {
            _result.Should().BeOfType<ClientCreateResult.Success>();
            ((ClientCreateResult.Success)_result).ClientUuid.Should().Be(_insertedApplicationId);
        }

        [Test]
        public void It_commits_without_rollback()
        {
            A.CallTo(() => _transaction.Commit()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _transaction.Rollback()).MustNotHaveHappened();
        }

        [Test]
        public void It_creates_the_role_scope_and_client_role()
        {
            A.CallTo(() => _dataRepository.InsertRoleAsync(A<Guid>._, RoleName, _connection, _transaction))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _dataRepository.InsertScopeAsync(A<Guid>._, ScopeName, _connection, _transaction))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    _dataRepository.InsertApplicationScopeAsync(
                        _insertedApplicationId,
                        A<Guid>._,
                        _connection,
                        _transaction
                    )
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    _dataRepository.InsertClientRoleAsync(
                        _insertedApplicationId,
                        A<Guid>._,
                        _connection,
                        _transaction
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_logs_no_duplicate_key_warning() => AssertNoDuplicateKeyWarning();

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    /// <summary>
    /// The data repository reports the key as already registered. The provider rolls the whole
    /// attempt back, the role it created included, writes nothing further, and classifies the
    /// result as a duplicate key with nothing to compensate. Fails against the implementation that
    /// let the engine exception reach the generic catch as an unknown failure.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_key_the_provider_already_holds : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeApplicationInsert(OpenIddictApplicationInsertOutcome.DuplicateClientId);

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_duplicate_client_id() =>
            _result.Should().BeOfType<ClientCreateResult.FailureDuplicateClientId>();

        [Test]
        public void It_rolls_the_transaction_back_without_committing() =>
            AssertTransactionRolledBackWithoutCommit();

        [Test]
        public void It_created_the_role_inside_the_rolled_back_transaction() =>
            A.CallTo(() => _dataRepository.InsertRoleAsync(A<Guid>._, RoleName, _connection, _transaction))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_inserts_no_scope_or_client_role() => AssertNothingWrittenAfterTheApplicationInsert();

        [Test]
        public void It_records_the_conflict_as_a_warning_with_the_client_identifier() =>
            _logger
                .LoggedEntries()
                .Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Warning
                    && entry.Message.Contains(DuplicateKeyWarning)
                    && entry.Message.Contains(ClientKey)
                );

        [Test]
        public void It_logs_nothing_at_error() =>
            _logger.LoggedEntries().Should().NotContain(entry => entry.Level == LogLevel.Error);

        [Test]
        public void It_attaches_no_exception_to_any_log_entry() =>
            _logger.LoggedEntries().Should().AllSatisfy(entry => entry.Exception.Should().BeNull());

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    [TestFixture]
    public class Given_a_key_conflict_for_a_role_that_already_exists : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _dataRepository.FindRoleIdByNameAsync(RoleName, _connection, _transaction))
                .Returns(Task.FromResult<Guid?>(Guid.NewGuid()));
            ArrangeApplicationInsert(OpenIddictApplicationInsertOutcome.DuplicateClientId);

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_duplicate_client_id() =>
            _result.Should().BeOfType<ClientCreateResult.FailureDuplicateClientId>();

        [Test]
        public void It_creates_no_role() =>
            A.CallTo(() =>
                    _dataRepository.InsertRoleAsync(
                        A<Guid>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_rolls_the_transaction_back_without_committing() =>
            AssertTransactionRolledBackWithoutCommit();
    }

    [TestFixture]
    public class Given_a_key_conflict_whose_client_id_needs_sanitizing : CreateClientTestBase
    {
        private const string RawClientId = "key\r\n<b>{x}</b>";
        private const string SanitizedClientId = "keybx/b";

        [SetUp]
        public async Task Act()
        {
            ArrangeApplicationInsert(OpenIddictApplicationInsertOutcome.DuplicateClientId);

            await ActCreateAsync(RawClientId);
        }

        [Test]
        public void It_returns_failure_duplicate_client_id() =>
            _result.Should().BeOfType<ClientCreateResult.FailureDuplicateClientId>();

        [Test]
        public void It_logs_the_sanitized_client_identifier() =>
            _logger
                .LoggedEntries()
                .Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Warning
                    && entry.Message.Contains(DuplicateKeyWarning)
                    && entry.Message.Contains(SanitizedClientId)
                );

        [Test]
        public void It_logs_none_of_the_raw_characters() =>
            _logger
                .LoggedMessages()
                .Should()
                .OnlyContain(message =>
                    !message.Contains('\r')
                    && !message.Contains('\n')
                    && !message.Contains('<')
                    && !message.Contains('>')
                    && !message.Contains('{')
                    && !message.Contains('}')
                );
    }

    /// <summary>
    /// Role and scope uniqueness violations are not classified: the data repository lets them
    /// throw, and the provider reports them as unknown failures exactly as before.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_role_insert_throws : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _dataRepository.InsertRoleAsync(
                        A<Guid>._,
                        RoleName,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Throws(new InvalidOperationException("duplicate role name"));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_does_not_commit() => A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();

        [Test]
        public void It_never_reaches_the_application_insert() =>
            A.CallTo(() =>
                    _dataRepository.InsertApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string>._,
                        A<string>._,
                        A<string[]>._,
                        A<string[]>._,
                        A<string>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_logs_the_failure_at_error() =>
            _logger.VerifyLogError("Failed to create OpenIddict client");

        [Test]
        public void It_logs_no_duplicate_key_warning() => AssertNoDuplicateKeyWarning();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_application_insert_throws : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _dataRepository.InsertApplicationAsync(
                        A<Guid>._,
                        A<string>._,
                        A<string>._,
                        A<string>._,
                        A<string[]>._,
                        A<string[]>._,
                        A<string>._,
                        A<string>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Throws(new InvalidOperationException("primary key collision"));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_does_not_commit() => A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();

        [Test]
        public void It_inserts_no_scope_or_client_role() => AssertNothingWrittenAfterTheApplicationInsert();

        [Test]
        public void It_logs_no_duplicate_key_warning() => AssertNoDuplicateKeyWarning();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_scope_insert_throws : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _dataRepository.InsertScopeAsync(
                        A<Guid>._,
                        ScopeName,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .Throws(new InvalidOperationException("duplicate scope name"));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_does_not_commit() => A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();

        [Test]
        public void It_assigns_no_client_role() =>
            A.CallTo(() =>
                    _dataRepository.InsertClientRoleAsync(
                        A<Guid>._,
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_logs_no_duplicate_key_warning() => AssertNoDuplicateKeyWarning();
    }
}
