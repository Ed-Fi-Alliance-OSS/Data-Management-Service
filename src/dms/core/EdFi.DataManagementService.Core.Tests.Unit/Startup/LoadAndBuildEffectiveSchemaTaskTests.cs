// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class LoadAndBuildEffectiveSchemaTaskTests
{
    private static ApiSchemaDocumentNodes CreateValidSchemaNodes()
    {
        var coreSchema = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "projectName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "isExtensionProject": false,
                    "resourceSchemas": {}
                }
            }
            """
        )!;
        return new ApiSchemaDocumentNodes(coreSchema, []);
    }

    [TestFixture]
    public class Given_Valid_Schema : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private IApiSchemaInputNormalizer _mockNormalizer = null!;
        private IEffectiveSchemaHashProvider _mockHashProvider = null!;
        private IResourceKeySeedProvider _mockSeedProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;

        [SetUp]
        public void Setup()
        {
            _schemaNodes = CreateValidSchemaNodes();

            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes()).Returns(_schemaNodes);
            A.CallTo(() => _mockSchemaProvider.IsSchemaValid).Returns(true);
            A.CallTo(() => _mockSchemaProvider.ApiSchemaFailures).Returns([]);

            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();

            _mockNormalizer = A.Fake<IApiSchemaInputNormalizer>();
            A.CallTo(() => _mockNormalizer.Normalize(A<ApiSchemaDocumentNodes>._))
                .ReturnsLazily((ApiSchemaDocumentNodes nodes) => nodes);

            _mockHashProvider = A.Fake<IEffectiveSchemaHashProvider>();
            A.CallTo(() => _mockHashProvider.ComputeHash(A<ApiSchemaDocumentNodes>._)).Returns(string.Empty);

            _mockSeedProvider = A.Fake<IResourceKeySeedProvider>();
            A.CallTo(() => _mockSeedProvider.GetSeeds(A<ApiSchemaDocumentNodes>._))
                .Returns(new List<ResourceKeySeed>());

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                _mockNormalizer,
                _mockHashProvider,
                _mockSeedProvider,
                NullLogger<LoadAndBuildEffectiveSchemaTask>.Instance
            );
        }

        [Test]
        public void It_has_order_100()
        {
            _task.Order.Should().Be(100);
        }

        [Test]
        public void It_has_expected_name()
        {
            _task.Name.Should().Be("Load and Build Effective Schema");
        }

        [Test]
        public async Task It_loads_schema_nodes()
        {
            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes()).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_normalizes_schema_nodes()
        {
            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            A.CallTo(() => _mockNormalizer.Normalize(_schemaNodes)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_computes_schema_hash()
        {
            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            A.CallTo(() => _mockHashProvider.ComputeHash(A<ApiSchemaDocumentNodes>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_derives_resource_key_seeds()
        {
            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            A.CallTo(() => _mockSeedProvider.GetSeeds(A<ApiSchemaDocumentNodes>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_initializes_effective_schema_provider()
        {
            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            A.CallTo(() => _mockEffectiveProvider.Initialize(A<ApiSchemaDocumentNodes>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Schema_Loading_Fails : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes())
                .Throws(new InvalidOperationException("Schema file not found"));

            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                A.Fake<IApiSchemaInputNormalizer>(),
                A.Fake<IEffectiveSchemaHashProvider>(),
                A.Fake<IResourceKeySeedProvider>(),
                NullLogger<LoadAndBuildEffectiveSchemaTask>.Instance
            );
        }

        [Test]
        public async Task It_throws_InvalidOperationException()
        {
            // Act
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("API schema loading failed");
        }

        [Test]
        public async Task It_does_not_initialize_effective_provider()
        {
            // Act
            try
            {
                await _task.ExecuteAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Assert
            A.CallTo(() => _mockEffectiveProvider.Initialize(A<ApiSchemaDocumentNodes>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Schema_Validation_Fails : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes()).Returns(CreateValidSchemaNodes());
            A.CallTo(() => _mockSchemaProvider.IsSchemaValid).Returns(false);
            A.CallTo(() => _mockSchemaProvider.ApiSchemaFailures)
                .Returns([
                    new ApiSchemaFailure("Validation", "Missing required field"),
                    new ApiSchemaFailure("Validation", "Invalid resource schema"),
                ]);

            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                A.Fake<IApiSchemaInputNormalizer>(),
                A.Fake<IEffectiveSchemaHashProvider>(),
                A.Fake<IResourceKeySeedProvider>(),
                NullLogger<LoadAndBuildEffectiveSchemaTask>.Instance
            );
        }

        [Test]
        public async Task It_throws_InvalidOperationException_with_failure_count()
        {
            // Act
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 error(s)*");
        }

        [Test]
        public async Task It_does_not_initialize_effective_provider()
        {
            // Act
            try
            {
                await _task.ExecuteAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Assert
            A.CallTo(() => _mockEffectiveProvider.Initialize(A<ApiSchemaDocumentNodes>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Cancellation_Is_Requested : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                A.Fake<IApiSchemaInputNormalizer>(),
                A.Fake<IEffectiveSchemaHashProvider>(),
                A.Fake<IResourceKeySeedProvider>(),
                NullLogger<LoadAndBuildEffectiveSchemaTask>.Instance
            );
        }

        [Test]
        public async Task It_throws_OperationCanceledException_before_loading()
        {
            // Arrange
            var cancelledToken = new CancellationToken(canceled: true);

            // Act
            Func<Task> act = async () => await _task.ExecuteAsync(cancelledToken);

            // Assert
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public async Task It_does_not_load_schema_when_cancelled()
        {
            // Arrange
            var cancelledToken = new CancellationToken(canceled: true);

            // Act
            try
            {
                await _task.ExecuteAsync(cancelledToken);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // Assert
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes()).MustNotHaveHappened();
        }
    }
}
