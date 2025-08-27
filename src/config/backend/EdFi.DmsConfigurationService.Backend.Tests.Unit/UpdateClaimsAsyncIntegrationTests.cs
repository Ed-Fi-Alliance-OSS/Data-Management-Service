// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class UpdateClaimsAsyncIntegrationTests
{
    private IClaimsProvider _claimsProvider = null!;
    private IClaimSetRepository _claimSetRepository = null!;
    private IClaimsHierarchyRepository _claimsHierarchyRepository = null!;
    private IClaimsTableValidator _claimsTableValidator = null!;
    private IClaimsDocumentRepository _claimsDocumentRepository = null!;
    private ILogger<Backend.ClaimsDataLoader.ClaimsDataLoader> _logger = null!;
    private Backend.ClaimsDataLoader.ClaimsDataLoader _claimsDataLoader = null!;

    [SetUp]
    public void Setup()
    {
        _claimsProvider = A.Fake<IClaimsProvider>();
        _claimSetRepository = A.Fake<IClaimSetRepository>();
        _claimsHierarchyRepository = A.Fake<IClaimsHierarchyRepository>();
        _claimsTableValidator = A.Fake<IClaimsTableValidator>();
        _claimsDocumentRepository = A.Fake<IClaimsDocumentRepository>();
        _logger = A.Fake<ILogger<Backend.ClaimsDataLoader.ClaimsDataLoader>>();

        _claimsDataLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
            _claimsProvider,
            _claimSetRepository,
            _claimsHierarchyRepository,
            _claimsTableValidator,
            _claimsDocumentRepository,
            _logger
        );
    }

    [TestFixture]
    public class Given_UpdateClaimsAsync_is_called : UpdateClaimsAsyncIntegrationTests
    {
        [Test]
        public async Task It_should_commit_transaction_on_success()
        {
            // Arrange
            var claimSetsJson = JsonNode.Parse(
                """
                [
                    { "claimSetName": "Test1", "isSystemReserved": false },
                    { "claimSetName": "Test2", "isSystemReserved": false }
                ]
                """
            );

            var hierarchyJson = JsonNode.Parse(
                """
                [
                    { "name": "domain1", "claims": [] }
                ]
                """
            );

            var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);

            // Mock successful update
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .Returns(new ClaimsDocumentUpdateResult.Success(0, 2, true));

            // Act
            var result = await _claimsDataLoader.UpdateClaimsAsync(claimsNodes);

            // Assert
            Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());
            var success = (ClaimsDataLoadResult.Success)result;
            Assert.That(success.ClaimSetsLoaded, Is.EqualTo(2));
            Assert.That(success.HierarchyLoaded, Is.True);

            // Verify ReplaceClaimsDocument was called
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_should_rollback_transaction_on_failure()
        {
            // Arrange
            var claimSetsJson = JsonNode.Parse(
                """[{ "claimSetName": "Test1", "isSystemReserved": false }]"""
            );
            var hierarchyJson = JsonNode.Parse("""[]""");
            var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);

            // Mock failed update
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .Returns(new ClaimsDocumentUpdateResult.DatabaseFailure("Database error occurred"));

            // Act
            var result = await _claimsDataLoader.UpdateClaimsAsync(claimsNodes);

            // Assert
            Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.DatabaseFailure>());
            var failure = (ClaimsDataLoadResult.DatabaseFailure)result;
            Assert.That(failure.ErrorMessage, Is.EqualTo("Database error occurred"));

            // Verify transaction was attempted
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_should_handle_foreign_key_constraint_errors()
        {
            // Arrange
            var claimSetsJson = JsonNode.Parse(
                """[{ "claimSetName": "SystemReserved", "isSystemReserved": true }]"""
            );
            var hierarchyJson = JsonNode.Parse("""[]""");
            var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);

            // Mock foreign key constraint failure
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .Returns(
                    new ClaimsDocumentUpdateResult.DatabaseFailure(
                        "Cannot delete claim set 'SystemReserved' - it is referenced by existing applications"
                    )
                );

            // Act
            var result = await _claimsDataLoader.UpdateClaimsAsync(claimsNodes);

            // Assert
            Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.DatabaseFailure>());
            var failure = (ClaimsDataLoadResult.DatabaseFailure)result;
            Assert.That(failure.ErrorMessage, Contains.Substring("referenced by existing applications"));
        }

        [Test]
        public async Task It_should_properly_sequence_operations_within_transaction()
        {
            // Arrange
            var claimSetsJson = JsonNode.Parse(
                """
                [
                    { "claimSetName": "NewSet1", "isSystemReserved": false },
                    { "claimSetName": "NewSet2", "isSystemReserved": false }
                ]
                """
            );

            var hierarchyJson = JsonNode.Parse(
                """
                [
                    {
                        "name": "domain1",
                        "claims": [
                            { "name": "claim1", "type": "read" }
                        ]
                    }
                ]
                """
            );

            var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);

            var callOrder = new List<string>();

            // Track call order
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .Invokes(() => callOrder.Add("ReplaceClaimsDocument"))
                .Returns(new ClaimsDocumentUpdateResult.Success(0, 2, true));

            // Act
            var result = await _claimsDataLoader.UpdateClaimsAsync(claimsNodes);

            // Assert
            Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());
            Assert.That(callOrder.Count, Is.EqualTo(1));
            Assert.That(callOrder[0], Is.EqualTo("ReplaceClaimsDocument"));
        }

        [Test]
        public async Task It_should_handle_empty_claims_document()
        {
            // Arrange
            var claimSetsJson = JsonNode.Parse("[]");
            var hierarchyJson = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);

            // Mock successful update with no claim sets
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .Returns(new ClaimsDocumentUpdateResult.Success(0, 0, true));

            // Act
            var result = await _claimsDataLoader.UpdateClaimsAsync(claimsNodes);

            // Assert
            Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());
            var success = (ClaimsDataLoadResult.Success)result;
            Assert.That(success.ClaimSetsLoaded, Is.EqualTo(0));
            Assert.That(success.HierarchyLoaded, Is.True);
        }

        [Test]
        public async Task It_should_handle_exception_during_update()
        {
            // Arrange
            var claimSetsJson = JsonNode.Parse("""[{ "claimSetName": "Test", "isSystemReserved": false }]""");
            var hierarchyJson = JsonNode.Parse("""[]""");
            var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);

            // Mock exception during update
            A.CallTo(() => _claimsDocumentRepository.ReplaceClaimsDocument(A<ClaimsDocument>._))
                .Throws(new InvalidOperationException("Unexpected database error"));

            // Act
            var result = await _claimsDataLoader.UpdateClaimsAsync(claimsNodes);

            // Assert
            Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.UnexpectedFailure>());
            var failure = (ClaimsDataLoadResult.UnexpectedFailure)result;
            Assert.That(failure.ErrorMessage, Is.EqualTo("Invalid operation during claims update"));
            Assert.That(failure.Exception, Is.TypeOf<InvalidOperationException>());
        }
    }
}
