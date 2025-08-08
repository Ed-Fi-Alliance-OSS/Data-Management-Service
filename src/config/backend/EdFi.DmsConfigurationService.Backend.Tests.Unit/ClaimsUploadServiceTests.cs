// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class ClaimsUploadServiceTests
{
    private ILogger<ClaimsUploadService> _logger = null!;
    private IClaimsProvider _claimsProvider = null!;
    private IClaimsDataLoader _claimsDataLoader = null!;
    private IClaimsValidator _claimsValidator = null!;
    private ClaimsUploadService _claimsUploadService = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<ClaimsUploadService>>();
        _claimsProvider = A.Fake<IClaimsProvider>();
        _claimsDataLoader = A.Fake<IClaimsDataLoader>();
        _claimsValidator = A.Fake<IClaimsValidator>();
        _claimsUploadService = new ClaimsUploadService(
            _logger,
            _claimsProvider,
            _claimsDataLoader,
            _claimsValidator
        );
    }

    [TestFixture]
    public class Given_UploadClaimsAsync_is_called : ClaimsUploadServiceTests
    {
        [Test]
        public async Task It_should_update_database_when_validation_succeeds()
        {
            // Arrange
            var claimsJson = JsonNode.Parse(
                """
                {
                    "claimSets": [{"claimSetName": "Test", "isSystemReserved": false}],
                    "claimsHierarchy": []
                }
                """
            );

            // Mock validator succeeds
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(new List<ClaimsValidationFailure>());

            // Mock successful database update
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .Returns(new ClaimsDataLoadResult.Success(1, true));

            // Act
            var result = await _claimsUploadService.UploadClaimsAsync(claimsJson!);

            // Assert
            Assert.That(result.Success, Is.True);
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_should_not_update_database_when_validation_fails()
        {
            // Arrange
            var claimsJson = JsonNode.Parse(
                """
                {
                    "claimSets": "invalid",
                    "claimsHierarchy": []
                }
                """
            );

            // Mock validator fails
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(
                    new List<ClaimsValidationFailure>
                    {
                        new(new JsonPath("$.claimSets"), new List<string> { "claimSets must be an array" }),
                    }
                );

            // Act
            var result = await _claimsUploadService.UploadClaimsAsync(claimsJson!);

            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._)).MustNotHaveHappened();
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_should_return_failure_when_database_update_fails()
        {
            // Arrange
            var claimsJson = JsonNode.Parse(
                """
                {
                    "claimSets": [{"claimSetName": "Test", "isSystemReserved": false}],
                    "claimsHierarchy": []
                }
                """
            );

            // Mock validator succeeds
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(new List<ClaimsValidationFailure>());

            // Mock database update failure
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .Returns(new ClaimsDataLoadResult.DatabaseFailure("Connection failed"));

            // Act
            var result = await _claimsUploadService.UploadClaimsAsync(claimsJson!);

            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            Assert.That(result.Failures[0].Message, Is.EqualTo("Connection failed"));
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_should_pass_correct_nodes_to_database_update()
        {
            // Arrange
            var claimSetsData = """[{"claimSetName": "Test", "isSystemReserved": false}]""";
            var hierarchyData = """[{"name": "domain1", "claims": []}]""";
            var claimsJson = JsonNode.Parse(
                $$"""
                {
                    "claimSets": {{claimSetsData}},
                    "claimsHierarchy": {{hierarchyData}}
                }
                """
            );

            ClaimsDocument? capturedNodes = null;

            // Mock validator succeeds
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(new List<ClaimsValidationFailure>());

            // Capture the nodes passed to UpdateClaimsAsync
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .Invokes((ClaimsDocument nodes) => capturedNodes = nodes)
                .Returns(new ClaimsDataLoadResult.Success(1, true));

            // Act
            await _claimsUploadService.UploadClaimsAsync(claimsJson!);

            // Assert
            Assert.That(capturedNodes, Is.Not.Null);
            // Verify the content matches by parsing and comparing the JSON nodes
            var capturedClaimSets = JsonNode.Parse(capturedNodes!.ClaimSetsNode.ToJsonString());
            var expectedClaimSets = JsonNode.Parse(claimSetsData);
            Assert.That(capturedClaimSets!.ToJsonString(), Is.EqualTo(expectedClaimSets!.ToJsonString()));

            var capturedHierarchy = JsonNode.Parse(capturedNodes.ClaimsHierarchyNode.ToJsonString());
            var expectedHierarchy = JsonNode.Parse(hierarchyData);
            Assert.That(capturedHierarchy!.ToJsonString(), Is.EqualTo(expectedHierarchy!.ToJsonString()));
        }

        [Test]
        public async Task It_should_not_update_provider_when_database_update_fails()
        {
            // Arrange
            var claimsJson = JsonNode.Parse(
                """
                {
                    "claimSets": [{"claimSetName": "Test", "isSystemReserved": false}],
                    "claimsHierarchy": []
                }
                """
            );

            // Mock validator succeeds
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(new List<ClaimsValidationFailure>());

            // Mock database update failure
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .Returns(new ClaimsDataLoadResult.DatabaseFailure("Connection failed"));

            // Act
            await _claimsUploadService.UploadClaimsAsync(claimsJson!);

            // Assert
            // Validator should only be called once for validation, UpdateInMemoryState should not be called after DB failure
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_ReloadClaimsAsync_is_called : ClaimsUploadServiceTests
    {
        [Test]
        public async Task It_should_update_database_when_provider_load_succeeds()
        {
            // Arrange
            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);

            // Mock provider returns valid claims
            A.CallTo(() => _claimsProvider.LoadClaimsFromSource())
                .Returns(new ClaimsLoadResult(claimsNodes, []));

            // Mock validator succeeds
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(new List<ClaimsValidationFailure>());

            // Mock successful database update
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .Returns(new ClaimsDataLoadResult.Success(1, true));

            // Act
            var result = await _claimsUploadService.ReloadClaimsAsync();

            // Assert
            Assert.That(result.Success, Is.True);
            A.CallTo(() => _claimsProvider.LoadClaimsFromSource()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_should_return_failure_when_provider_load_fails()
        {
            // Arrange
            var failures = new List<ClaimsFailure>
            {
                new("Configuration", "Could not load Claims.json file"),
            };

            // Mock provider returns failures
            A.CallTo(() => _claimsProvider.LoadClaimsFromSource())
                .Returns(new ClaimsLoadResult(null, failures));

            // Act
            var result = await _claimsUploadService.ReloadClaimsAsync();

            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            Assert.That(result.Failures[0].Message, Is.EqualTo("Could not load Claims.json file"));
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._)).MustNotHaveHappened();
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_should_not_update_database_when_validation_fails()
        {
            // Arrange
            var claimSetsNode = JsonNode.Parse("[{\"invalid\": \"data\"}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);

            // Mock provider returns claims
            A.CallTo(() => _claimsProvider.LoadClaimsFromSource())
                .Returns(new ClaimsLoadResult(claimsNodes, []));

            // Mock validator fails
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(
                    new List<ClaimsValidationFailure>
                    {
                        new(
                            new JsonPath("$.claimSets[0]"),
                            new List<string> { "Missing required claimSetName" }
                        ),
                    }
                );

            // Act
            var result = await _claimsUploadService.ReloadClaimsAsync();

            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._)).MustNotHaveHappened();
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_should_return_failure_when_database_update_fails()
        {
            // Arrange
            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);

            // Mock provider returns valid claims
            A.CallTo(() => _claimsProvider.LoadClaimsFromSource())
                .Returns(new ClaimsLoadResult(claimsNodes, []));

            // Mock validator succeeds
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(new List<ClaimsValidationFailure>());

            // Mock database update failure
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .Returns(new ClaimsDataLoadResult.DatabaseFailure("Database connection lost"));

            // Act
            var result = await _claimsUploadService.ReloadClaimsAsync();

            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            Assert.That(result.Failures[0].Message, Is.EqualTo("Database connection lost"));
            A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_should_only_update_provider_after_successful_database_update()
        {
            // Arrange
            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);

            // Mock provider returns valid claims
            A.CallTo(() => _claimsProvider.LoadClaimsFromSource())
                .Returns(new ClaimsLoadResult(claimsNodes, []));

            // Mock validator succeeds
            A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._))
                .Returns(new List<ClaimsValidationFailure>());

            // Mock successful database update
            A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                .Returns(new ClaimsDataLoadResult.Success(1, true));

            // Act
            var result = await _claimsUploadService.ReloadClaimsAsync();

            // Assert
            Assert.That(result.Success, Is.True);

            // Verify the sequence: LoadClaimsFromSource -> Validate -> UpdateClaimsAsync -> UpdateInMemoryState
            A.CallTo(() => _claimsProvider.LoadClaimsFromSource())
                .MustHaveHappened()
                .Then(A.CallTo(() => _claimsValidator.Validate(A<JsonNode>._)).MustHaveHappened())
                .Then(
                    A.CallTo(() => _claimsDataLoader.UpdateClaimsAsync(A<ClaimsDocument>._))
                        .MustHaveHappened()
                )
                .Then(
                    A.CallTo(() => _claimsProvider.UpdateInMemoryState(A<ClaimsDocument>._, A<Guid>._))
                        .MustHaveHappened()
                );
        }
    }
}
