// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
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
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(7));

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
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(7));

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
                .Invokes(call => capturedProtocolMappers = call.Arguments.Get<string>(7));

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
                    _dataRepository.UpdateClientSecretAsync(A<Guid>._, A<string>._, A<IDbConnection>._)
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
                    _dataRepository.UpdateClientSecretAsync(A<Guid>._, A<string>._, A<IDbConnection>._)
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

        protected async Task ActUpdateAsync(string? clientUuid = null) =>
            _result = await _repository.UpdateClientNamespaceClaimAsync(
                clientUuid ?? _clientUuid.ToString(),
                NewPrefixes
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
}
