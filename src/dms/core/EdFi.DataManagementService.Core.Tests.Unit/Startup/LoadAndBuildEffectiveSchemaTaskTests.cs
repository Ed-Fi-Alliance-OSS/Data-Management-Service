// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
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
                "apiSchemaVersion": "1.0.0",
                "projectSchema": {
                    "projectName": "Ed-Fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "isExtensionProject": false,
                    "abstractResources": {},
                    "resourceSchemas": {
                        "students": {
                            "resourceName": "Student",
                            "isDescriptor": false,
                            "isSchoolYearEnumeration": false,
                            "isResourceExtension": false,
                            "allowIdentityUpdates": false,
                            "isSubclass": false,
                            "identityJsonPaths": ["$.studentUniqueId"],
                            "booleanJsonPaths": [],
                            "numericJsonPaths": [],
                            "dateJsonPaths": [],
                            "dateTimeJsonPaths": [],
                            "equalityConstraints": [],
                            "arrayUniquenessConstraints": [],
                            "documentPathsMapping": {
                                "StudentUniqueId": {
                                    "isReference": false,
                                    "isPartOfIdentity": true,
                                    "isRequired": true,
                                    "path": "$.studentUniqueId"
                                }
                            },
                            "queryFieldMapping": {},
                            "securableElements": {
                                "Namespace": [],
                                "EducationOrganization": [],
                                "Student": [],
                                "Contact": [],
                                "Staff": []
                            },
                            "authorizationPathways": [],
                            "decimalPropertyValidationInfos": [],
                            "jsonSchemaForInsert": {
                                "type": "object",
                                "properties": {
                                    "studentUniqueId": {
                                        "type": "string"
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """
        )!;
        return new ApiSchemaDocumentNodes(coreSchema, []);
    }

    private static EffectiveSchemaSetBuilder CreateBuilder(
        IEffectiveSchemaHashProvider hashProvider,
        IResourceKeySeedProvider seedProvider
    ) => new(hashProvider, seedProvider);

    [TestFixture]
    public class Given_Valid_Schema : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private IEffectiveSchemaSetProvider _mockEffectiveSchemaSetProvider = null!;
        private IApiSchemaInputNormalizer _mockNormalizer = null!;
        private IEffectiveSchemaHashProvider _mockHashProvider = null!;
        private IResourceKeySeedProvider _mockSeedProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;

        [SetUp]
        public void Setup()
        {
            _schemaNodes = CreateValidSchemaNodes();

            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes()).Returns(_schemaNodes);
            A.CallTo(() => _mockSchemaProvider.IsSchemaValid).Returns(true);
            A.CallTo(() => _mockSchemaProvider.ApiSchemaFailures).Returns([]);

            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();
            _mockEffectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            _mockNormalizer = A.Fake<IApiSchemaInputNormalizer>();
            A.CallTo(() => _mockNormalizer.Normalize(A<ApiSchemaDocumentNodes>._))
                .ReturnsLazily(
                    (ApiSchemaDocumentNodes nodes) => new ApiSchemaNormalizationResult.SuccessResult(nodes)
                );

            _mockHashProvider = A.Fake<IEffectiveSchemaHashProvider>();
            A.CallTo(() =>
                    _mockHashProvider.ComputeHash(A<string>._, A<IReadOnlyList<ProjectSchemaMetadata>>._)
                )
                .Returns("expected-schema-hash");

            _mockSeedProvider = A.Fake<IResourceKeySeedProvider>();
            _seeds =
            [
                new ResourceKeySeed(
                    ResourceKeyId: 1,
                    ProjectName: "Ed-Fi",
                    ResourceName: "Student",
                    ResourceVersion: "5.0.0",
                    IsAbstract: false
                ),
            ];
            A.CallTo(() => _mockSeedProvider.GetSeeds(_schemaNodes)).Returns(_seeds);
            A.CallTo(() => _mockSeedProvider.ComputeSeedHash(_seeds)).Returns([0x12, 0x34]);

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                _mockEffectiveSchemaSetProvider,
                _mockNormalizer,
                CreateBuilder(_mockHashProvider, _mockSeedProvider),
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
            A.CallTo(() =>
                    _mockHashProvider.ComputeHash(A<string>._, A<IReadOnlyList<ProjectSchemaMetadata>>._)
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_derives_resource_key_seeds()
        {
            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            A.CallTo(() => _mockSeedProvider.GetSeeds(_schemaNodes)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _mockSeedProvider.ComputeSeedHash(_seeds)).MustHaveHappenedOnceExactly();
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

        [Test]
        public async Task It_initializes_effective_schema_set_provider()
        {
            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            A.CallTo(() =>
                    _mockEffectiveSchemaSetProvider.Initialize(
                        A<EffectiveSchemaSet>.That.Matches(set =>
                            set.EffectiveSchema.EffectiveSchemaHash == "expected-schema-hash"
                            && set.EffectiveSchema.ResourceKeyCount == 1
                            && set.ProjectsInEndpointOrder.Count == 1
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_initializes_the_effective_schema_set_provider_before_the_effective_api_schema_provider()
        {
            // Arrange
            List<string> initializationOrder = [];

            A.CallTo(() => _mockEffectiveSchemaSetProvider.Initialize(A<EffectiveSchemaSet>._))
                .Invokes(() => initializationOrder.Add("schema-set"));

            A.CallTo(() => _mockEffectiveProvider.Initialize(A<ApiSchemaDocumentNodes>._))
                .Invokes(() => initializationOrder.Add("effective-api"));

            // Act
            await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            initializationOrder.Should().Equal("schema-set", "effective-api");
        }
    }

    [TestFixture]
    public class Given_Effective_Schema_Set_Initialization_Fails : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private IEffectiveSchemaSetProvider _mockEffectiveSchemaSetProvider = null!;
        private IApiSchemaInputNormalizer _mockNormalizer = null!;
        private IEffectiveSchemaHashProvider _mockHashProvider = null!;
        private IResourceKeySeedProvider _mockSeedProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;
        private InvalidOperationException _expectedException = null!;
        private bool _effectiveApiSchemaProviderIsInitialized;

        [SetUp]
        public void Setup()
        {
            var schemaNodes = CreateValidSchemaNodes();

            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes()).Returns(schemaNodes);
            A.CallTo(() => _mockSchemaProvider.IsSchemaValid).Returns(true);
            A.CallTo(() => _mockSchemaProvider.ApiSchemaFailures).Returns([]);

            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();
            A.CallTo(() => _mockEffectiveProvider.Initialize(A<ApiSchemaDocumentNodes>._))
                .Invokes(() => _effectiveApiSchemaProviderIsInitialized = true);
            A.CallTo(() => _mockEffectiveProvider.IsInitialized)
                .ReturnsLazily(() => _effectiveApiSchemaProviderIsInitialized);

            _mockEffectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            _expectedException = new InvalidOperationException("Schema-set initialization failed");
            A.CallTo(() => _mockEffectiveSchemaSetProvider.Initialize(A<EffectiveSchemaSet>._))
                .Throws(_expectedException);
            A.CallTo(() => _mockEffectiveSchemaSetProvider.IsInitialized).Returns(false);

            _mockNormalizer = A.Fake<IApiSchemaInputNormalizer>();
            A.CallTo(() => _mockNormalizer.Normalize(A<ApiSchemaDocumentNodes>._))
                .ReturnsLazily(
                    (ApiSchemaDocumentNodes nodes) => new ApiSchemaNormalizationResult.SuccessResult(nodes)
                );

            _mockHashProvider = A.Fake<IEffectiveSchemaHashProvider>();
            A.CallTo(() =>
                    _mockHashProvider.ComputeHash(A<string>._, A<IReadOnlyList<ProjectSchemaMetadata>>._)
                )
                .Returns("expected-schema-hash");

            _mockSeedProvider = A.Fake<IResourceKeySeedProvider>();
            IReadOnlyList<ResourceKeySeed> seeds =
            [
                new ResourceKeySeed(
                    ResourceKeyId: 1,
                    ProjectName: "Ed-Fi",
                    ResourceName: "Student",
                    ResourceVersion: "5.0.0",
                    IsAbstract: false
                ),
            ];
            A.CallTo(() => _mockSeedProvider.GetSeeds(schemaNodes)).Returns(seeds);
            A.CallTo(() => _mockSeedProvider.ComputeSeedHash(seeds)).Returns([0x12, 0x34]);

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                _mockEffectiveSchemaSetProvider,
                _mockNormalizer,
                CreateBuilder(_mockHashProvider, _mockSeedProvider),
                NullLogger<LoadAndBuildEffectiveSchemaTask>.Instance
            );
        }

        [Test]
        public async Task It_surfaces_the_original_exception()
        {
            // Act
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .Where(ex => ReferenceEquals(ex, _expectedException));
        }

        [Test]
        public async Task It_does_not_leave_the_providers_partially_initialized()
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
            _mockEffectiveSchemaSetProvider.IsInitialized.Should().BeFalse();
            _mockEffectiveProvider.IsInitialized.Should().BeFalse();
            A.CallTo(() => _mockEffectiveProvider.Initialize(A<ApiSchemaDocumentNodes>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Schema_Loading_Fails : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private IEffectiveSchemaSetProvider _mockEffectiveSchemaSetProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _mockSchemaProvider.GetApiSchemaNodes())
                .Throws(new InvalidOperationException("Schema file not found"));

            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();
            _mockEffectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                _mockEffectiveSchemaSetProvider,
                A.Fake<IApiSchemaInputNormalizer>(),
                CreateBuilder(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>()),
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
            A.CallTo(() => _mockEffectiveSchemaSetProvider.Initialize(A<EffectiveSchemaSet>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Schema_Validation_Fails : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private IEffectiveSchemaSetProvider _mockEffectiveSchemaSetProvider = null!;
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
            _mockEffectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                _mockEffectiveSchemaSetProvider,
                A.Fake<IApiSchemaInputNormalizer>(),
                CreateBuilder(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>()),
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
            A.CallTo(() => _mockEffectiveSchemaSetProvider.Initialize(A<EffectiveSchemaSet>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Cancellation_Is_Requested : LoadAndBuildEffectiveSchemaTaskTests
    {
        private IApiSchemaProvider _mockSchemaProvider = null!;
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private IEffectiveSchemaSetProvider _mockEffectiveSchemaSetProvider = null!;
        private LoadAndBuildEffectiveSchemaTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _mockSchemaProvider = A.Fake<IApiSchemaProvider>();
            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();
            _mockEffectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            _task = new LoadAndBuildEffectiveSchemaTask(
                _mockSchemaProvider,
                _mockEffectiveProvider,
                _mockEffectiveSchemaSetProvider,
                A.Fake<IApiSchemaInputNormalizer>(),
                CreateBuilder(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>()),
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
