// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class OpenIddictClientRepositoryTests
{
    private ILogger<OpenIddictClientRepository> _logger = null!;
    private IClientSecretHasher _secretHasher = null!;
    private IOpenIddictDataRepository _dataRepository = null!;
    private OpenIddictClientRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<OpenIddictClientRepository>>();
        _secretHasher = A.Fake<IClientSecretHasher>();
        _dataRepository = A.Fake<IOpenIddictDataRepository>();
        var connection = A.Fake<IDbConnection>();
        var transaction = A.Fake<IDbTransaction>();

        A.CallTo(() => _dataRepository.CreateConnectionAsync()).Returns(connection);
        A.CallTo(() => _dataRepository.BeginTransactionAsync(connection)).Returns(transaction);
        A.CallTo(() => _secretHasher.HashSecretAsync(A<string>.Ignored))
            .Returns(Task.FromResult("hashed_secret"));

        _repository = new OpenIddictClientRepository(_logger, _secretHasher, _dataRepository);
    }

    [TestFixture]
    public class Given_CreateClientAsync_With_DmsInstanceIds : OpenIddictClientRepositoryTests
    {
        [Test]
        public async Task It_should_include_dmsInstanceIds_claim_when_provided()
        {
            // Arrange
            var dmsInstanceIds = new long[] { 3, 1, 2 }; // Unsorted to test sorting

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
                dmsInstanceIds
            );

            // Assert
            result.Should().BeOfType<ClientCreateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dmsInstanceIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dmsInstanceIds"
            );

            dmsInstanceIdsClaim.Should().NotBeNull("dmsInstanceIds claim should be present");
            dmsInstanceIdsClaim!["claim.value"].Should().Be("1,2,3", "IDs should be sorted");
        }

        [Test]
        public async Task It_should_not_include_dmsInstanceIds_claim_when_null()
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
                null // No dmsInstanceIds
            );

            // Assert
            result.Should().BeOfType<ClientCreateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dmsInstanceIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dmsInstanceIds"
            );

            dmsInstanceIdsClaim.Should().BeNull("dmsInstanceIds claim should not be present when null");
        }

        [Test]
        public async Task It_should_handle_empty_dmsInstanceIds_array()
        {
            // Arrange
            var dmsInstanceIds = Array.Empty<long>();

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
                dmsInstanceIds
            );

            // Assert
            result.Should().BeOfType<ClientCreateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dmsInstanceIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dmsInstanceIds"
            );

            dmsInstanceIdsClaim.Should().NotBeNull("dmsInstanceIds claim should be present even when empty");
            dmsInstanceIdsClaim!["claim.value"].Should().Be("", "Empty array should result in empty string");
        }
    }

    [TestFixture]
    public class Given_UpdateClientAsync_With_DmsInstanceIds : OpenIddictClientRepositoryTests
    {
        [Test]
        public async Task It_should_merge_dmsInstanceIds_claim_when_provided()
        {
            // Arrange
            var clientUuid = Guid.NewGuid().ToString();
            var dmsInstanceIds = new long[] { 5, 3, 4 };

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
                dmsInstanceIds
            );

            // Assert
            result.Should().BeOfType<ClientUpdateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dmsInstanceIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dmsInstanceIds"
            );

            dmsInstanceIdsClaim.Should().NotBeNull("dmsInstanceIds claim should be present");
            dmsInstanceIdsClaim!["claim.value"].Should().Be("3,4,5", "IDs should be sorted");
        }

        [Test]
        public async Task It_should_update_existing_dmsInstanceIds_claim()
        {
            // Arrange
            var clientUuid = Guid.NewGuid().ToString();
            var newDmsInstanceIds = new long[] { 10, 20 };

            var existingProtocolMappers = JsonSerializer.Serialize(
                new List<Dictionary<string, string>>
                {
                    new() { { "claim.name", "namespacePrefixes" }, { "claim.value", "uri://test" } },
                    new() { { "claim.name", "educationOrganizationIds" }, { "claim.value", "100" } },
                    new() { { "claim.name", "dmsInstanceIds" }, { "claim.value", "1,2,3" } }, // Old value
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
                newDmsInstanceIds
            );

            // Assert
            result.Should().BeOfType<ClientUpdateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dmsInstanceIdsClaims = protocolMappers!
                .Where(m => m.ContainsKey("claim.name") && m["claim.name"] == "dmsInstanceIds")
                .ToList();

            dmsInstanceIdsClaims.Should().HaveCount(1, "Should have only one dmsInstanceIds claim");
            dmsInstanceIdsClaims[0]["claim.value"].Should().Be("10,20", "Should have new sorted IDs");
        }

        [Test]
        public async Task It_should_not_modify_dmsInstanceIds_when_null()
        {
            // Arrange
            var clientUuid = Guid.NewGuid().ToString();

            var existingProtocolMappers = JsonSerializer.Serialize(
                new List<Dictionary<string, string>>
                {
                    new() { { "claim.name", "namespacePrefixes" }, { "claim.value", "uri://test" } },
                    new() { { "claim.name", "educationOrganizationIds" }, { "claim.value", "100" } },
                    new() { { "claim.name", "dmsInstanceIds" }, { "claim.value", "1,2,3" } },
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
                null // Don't update dmsInstanceIds
            );

            // Assert
            result.Should().BeOfType<ClientUpdateResult.Success>();
            capturedProtocolMappers.Should().NotBeNull();

            var protocolMappers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                capturedProtocolMappers!
            );
            protocolMappers.Should().NotBeNull();

            var dmsInstanceIdsClaim = protocolMappers!.Find(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "dmsInstanceIds"
            );

            // When null is passed, the existing dmsInstanceIds should be preserved
            dmsInstanceIdsClaim.Should().NotBeNull("Existing dmsInstanceIds claim should be preserved");
            dmsInstanceIdsClaim!["claim.value"].Should().Be("1,2,3", "Original value should be unchanged");
        }
    }
}
