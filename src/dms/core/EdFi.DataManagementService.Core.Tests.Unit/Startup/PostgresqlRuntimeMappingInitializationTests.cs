// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Old.Postgresql;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class PostgresqlRuntimeMappingInitializationTests
{
    private const string MinimalRuntimeSchemaJson = """
        {
          "apiSchemaVersion": "1.0.0",
          "projectSchema": {
            "projectName": "Ed-Fi",
            "projectVersion": "5.0.0",
            "projectEndpointName": "ed-fi",
            "isExtensionProject": false,
            "description": "Test schema",
            "resourceNameMapping": {},
            "caseInsensitiveEndpointNameMapping": {},
            "educationOrganizationHierarchy": {},
            "educationOrganizationTypes": [],
            "resourceSchemas": {
              "students": {
                "resourceName": "Student",
                "isDescriptor": false,
                "isSchoolYearEnumeration": false,
                "isResourceExtension": false,
                "allowIdentityUpdates": false,
                "isSubclass": false,
                "identityJsonPaths": [
                  "$.studentUniqueId"
                ],
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
                  },
                  "FirstName": {
                    "isReference": false,
                    "isPartOfIdentity": false,
                    "isRequired": true,
                    "path": "$.firstName"
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
                      "type": "string",
                      "maxLength": 32
                    },
                    "firstName": {
                      "type": "string",
                      "maxLength": 75
                    }
                  },
                  "required": [
                    "studentUniqueId",
                    "firstName"
                  ]
                }
              }
            },
            "abstractResources": {}
          }
        }
        """;

    private static ApiSchemaDocumentNodes CreateSchemaNodes()
    {
        return new ApiSchemaDocumentNodes(JsonNode.Parse(MinimalRuntimeSchemaJson)!, []);
    }

    private static IApiSchemaProvider CreateApiSchemaProvider(ApiSchemaDocumentNodes schemaNodes)
    {
        var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

        A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(schemaNodes);
        A.CallTo(() => apiSchemaProvider.IsSchemaValid).Returns(true);
        A.CallTo(() => apiSchemaProvider.ApiSchemaFailures).Returns([]);

        return apiSchemaProvider;
    }

    [TestFixture]
    public class Given_Dms_Startup_Runs_With_Postgresql_Runtime_Mapping_Initialization
        : PostgresqlRuntimeMappingInitializationTests
    {
        private ServiceProvider _serviceProvider = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;

        [SetUp]
        public void Setup()
        {
            _schemaNodes = CreateSchemaNodes();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IApiSchemaProvider>(CreateApiSchemaProvider(_schemaNodes));
            services.AddSingleton<ICompiledSchemaCache, CompiledSchemaCache>();
            services.AddSingleton<EffectiveApiSchemaProvider>();
            services.AddSingleton<IEffectiveApiSchemaProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<EffectiveApiSchemaProvider>()
            );
            services.AddSingleton<DmsStartupOrchestrator>();
            services.AddSingleton<IDmsStartupTask, LoadAndBuildEffectiveSchemaTask>();
            services.AddSingleton<IDmsStartupTask, BackendMappingInitializationTask>();
            services.AddSingleton<IApiSchemaInputNormalizer, ApiSchemaInputNormalizer>();
            services.AddSingleton<IEffectiveSchemaHashProvider, EffectiveSchemaHashProvider>();
            services.AddSingleton<IResourceKeySeedProvider, ResourceKeySeedProvider>();
            services.AddPostgresqlDatastore();

            _serviceProvider = services.BuildServiceProvider();
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_primes_the_runtime_mapping_cache_before_requests_begin()
        {
            await _serviceProvider
                .GetRequiredService<DmsStartupOrchestrator>()
                .RunAllAsync(CancellationToken.None);

            var effectiveSchemaSetBuilder = _serviceProvider.GetRequiredService<EffectiveSchemaSetBuilder>();
            var inputNormalizer = _serviceProvider.GetRequiredService<IApiSchemaInputNormalizer>();
            var normalizedNodes = inputNormalizer.Normalize(_schemaNodes);
            var successResult = normalizedNodes
                .Should()
                .BeOfType<ApiSchemaNormalizationResult.SuccessResult>()
                .Subject;
            var effectiveSchemaSet = effectiveSchemaSetBuilder.Build(successResult.NormalizedNodes);
            var mappingSetKey = new MappingSetKey(
                EffectiveSchemaHash: effectiveSchemaSet.EffectiveSchema.EffectiveSchemaHash,
                Dialect: SqlDialect.Pgsql,
                RelationalMappingVersion: effectiveSchemaSet.EffectiveSchema.RelationalMappingVersion
            );

            var cache = _serviceProvider.GetRequiredService<MappingSetCache>();
            var cacheResult = await cache.GetOrCreateWithCacheStatusAsync(
                mappingSetKey,
                CancellationToken.None
            );

            cacheResult.WasCacheHit.Should().BeTrue();
            cacheResult.MappingSet.Key.Should().Be(mappingSetKey);
        }
    }

    [TestFixture]
    public class Given_Postgresql_Datastore_Services_Are_Registered
        : PostgresqlRuntimeMappingInitializationTests
    {
        private IServiceCollection _services = null!;

        [SetUp]
        public void Setup()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

            _services = new ServiceCollection();
            _services.AddLogging();
            _services.AddOptions();
            _services.AddSingleton<IHostApplicationLifetime>(A.Fake<IHostApplicationLifetime>());
            _services.AddSingleton<IApiSchemaProvider>(CreateApiSchemaProvider(CreateSchemaNodes()));
            _services.Configure<DatabaseOptions>(_ => { });

            _services
                .AddDmsDefaultConfiguration(
                    new LoggerConfiguration().CreateLogger(),
                    configuration.GetSection("CircuitBreaker"),
                    configuration.GetSection("DeadlockRetry"),
                    false
                )
                .AddPostgresqlDatastore()
                .AddPostgresqlQueryHandler();
        }

        [Test]
        public void It_replaces_the_core_no_op_backend_initializer()
        {
            var initializerDescriptors = _services
                .Where(descriptor => descriptor.ServiceType == typeof(IBackendMappingInitializer))
                .ToArray();

            initializerDescriptors.Should().ContainSingle();
            initializerDescriptors[0]
                .ImplementationType!.Name.Should()
                .Be("PostgresqlBackendMappingInitializer");
        }

        [Test]
        public void It_keeps_the_legacy_postgresql_datastore_and_query_handler_registrations()
        {
            _services
                .Should()
                .Contain(descriptor =>
                    descriptor.ServiceType == typeof(IDocumentStoreRepository)
                    && descriptor.ImplementationType == typeof(PostgresqlDocumentStoreRepository)
                    && descriptor.Lifetime == ServiceLifetime.Scoped
                );

            _services
                .Should()
                .Contain(descriptor =>
                    descriptor.ServiceType == typeof(IQueryHandler)
                    && descriptor.ImplementationType == typeof(PostgresqlDocumentStoreRepository)
                    && descriptor.Lifetime == ServiceLifetime.Scoped
                );
        }
    }
}
