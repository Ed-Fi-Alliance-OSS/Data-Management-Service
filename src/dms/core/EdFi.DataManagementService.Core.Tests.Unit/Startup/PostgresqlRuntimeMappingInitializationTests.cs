// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Backend.RelationalModel.SetPasses;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Old.Postgresql;
using EdFi.DataManagementService.Old.Postgresql.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static ApiSchemaDocumentNodes CreateSchemaNodes(
        JsonObject projectSchema,
        params JsonObject[] extensionProjectSchemas
    )
    {
        return new ApiSchemaDocumentNodes(
            new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectSchema },
            [
                .. extensionProjectSchemas.Select(static extensionProjectSchema =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["apiSchemaVersion"] = "1.0.0",
                            ["projectSchema"] = extensionProjectSchema,
                        }
                ),
            ]
        );
    }

    private static IApiSchemaProvider CreateApiSchemaProvider(ApiSchemaDocumentNodes schemaNodes)
    {
        var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

        A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(schemaNodes);
        A.CallTo(() => apiSchemaProvider.IsSchemaValid).Returns(true);
        A.CallTo(() => apiSchemaProvider.ApiSchemaFailures).Returns([]);

        return apiSchemaProvider;
    }

    private static AppSettings CreateAppSettings(bool validateProvisionedMappingsOnStartup = false) =>
        new()
        {
            AllowIdentityUpdateOverrides = "",
            ValidateProvisionedMappingsOnStartup = validateProvisionedMappingsOnStartup,
        };

    private static ServiceProvider CreateStartupServiceProvider(
        ApiSchemaDocumentNodes schemaNodes,
        IDmsInstanceProvider dmsInstanceProvider,
        IPostgresqlRuntimeDatabaseMetadataReader databaseMetadataReader,
        AppSettings? appSettings = null,
        ILogger<PostgresqlBackendMappingInitializer>? backendMappingInitializerLogger = null
    ) =>
        CreateStartupServiceProvider(
            CreateApiSchemaProvider(schemaNodes),
            dmsInstanceProvider,
            databaseMetadataReader,
            appSettings,
            backendMappingInitializerLogger
        );

    private static ServiceProvider CreateStartupServiceProvider(
        IApiSchemaProvider apiSchemaProvider,
        IDmsInstanceProvider dmsInstanceProvider,
        IPostgresqlRuntimeDatabaseMetadataReader databaseMetadataReader,
        AppSettings? appSettings = null,
        ILogger<PostgresqlBackendMappingInitializer>? backendMappingInitializerLogger = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(appSettings ?? CreateAppSettings()));

        if (backendMappingInitializerLogger is not null)
        {
            services.AddSingleton(backendMappingInitializerLogger);
        }

        services.AddSingleton(apiSchemaProvider);
        services.AddSingleton<ICompiledSchemaCache, CompiledSchemaCache>();
        services.AddSingleton<EffectiveApiSchemaProvider>();
        services.AddSingleton<IEffectiveApiSchemaProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<EffectiveApiSchemaProvider>()
        );
        services.AddSingleton<EffectiveSchemaSetBuilder>();
        services.AddSingleton<IEffectiveSchemaSetProvider, EffectiveSchemaSetProvider>();
        services.AddSingleton<DmsStartupOrchestrator>();
        services.AddSingleton<IDmsStartupTask, LoadAndBuildEffectiveSchemaTask>();
        services.AddSingleton<IDmsStartupTask, BackendMappingInitializationTask>();
        services.AddSingleton<IApiSchemaInputNormalizer, ApiSchemaInputNormalizer>();
        services.AddSingleton<IEffectiveSchemaHashProvider, EffectiveSchemaHashProvider>();
        services.AddSingleton<IResourceKeySeedProvider, ResourceKeySeedProvider>();
        services.AddPostgresqlDatastore();
        services.AddSingleton(dmsInstanceProvider);
        services.AddSingleton(databaseMetadataReader);

        return services.BuildServiceProvider();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static EffectiveSchemaSet BuildEffectiveSchemaSet(ApiSchemaDocumentNodes schemaNodes)
    {
        var inputNormalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
        var normalizationResult = inputNormalizer.Normalize(schemaNodes);

        if (normalizationResult is not ApiSchemaNormalizationResult.SuccessResult successResult)
        {
            throw new InvalidOperationException(
                $"Expected schema normalization to succeed, but got '{normalizationResult.GetType().Name}'."
            );
        }

        return new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        ).Build(successResult.NormalizedNodes);
    }

    private static MappingSetKey CreateMappingSetKey(EffectiveSchemaSet effectiveSchemaSet)
    {
        return new MappingSetKey(
            EffectiveSchemaHash: effectiveSchemaSet.EffectiveSchema.EffectiveSchemaHash,
            Dialect: SqlDialect.Pgsql,
            RelationalMappingVersion: effectiveSchemaSet.EffectiveSchema.RelationalMappingVersion
        );
    }

    private static async Task RunStartupInitializationPhasesAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default
    )
    {
        var orchestrator = serviceProvider.GetRequiredService<DmsStartupOrchestrator>();

        await orchestrator.RunByOrderRangeAsync(
            0,
            DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum,
            cancellationToken
        );
        await orchestrator.RunByOrderRangeAsync(
            DmsStartupTaskOrderRanges.BackendMappingMinimum,
            DmsStartupTaskOrderRanges.BackendMappingMaximum,
            cancellationToken
        );
    }

    private static PostgresqlDatabaseFingerprint CreateMatchingFingerprint(
        EffectiveSchemaSet effectiveSchemaSet
    )
    {
        return new PostgresqlDatabaseFingerprint(
            effectiveSchemaSet.EffectiveSchema.ApiSchemaFormatVersion,
            effectiveSchemaSet.EffectiveSchema.EffectiveSchemaHash,
            effectiveSchemaSet.EffectiveSchema.ResourceKeyCount,
            effectiveSchemaSet.EffectiveSchema.ResourceKeySeedHash
        );
    }

    private static JsonObject CreateProjectSchema(
        JsonObject resourceSchemas,
        string projectName = "Ed-Fi",
        string projectEndpointName = "ed-fi",
        bool isExtensionProject = false
    )
    {
        return new JsonObject
        {
            ["projectName"] = projectName,
            ["projectVersion"] = "5.0.0",
            ["projectEndpointName"] = projectEndpointName,
            ["isExtensionProject"] = isExtensionProject,
            ["description"] = "Test schema",
            ["resourceNameMapping"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
            ["educationOrganizationHierarchy"] = new JsonObject(),
            ["educationOrganizationTypes"] = new JsonArray(),
            ["resourceSchemas"] = resourceSchemas,
            ["abstractResources"] = new JsonObject(),
        };
    }

    private static JsonObject CreateCommonResourceSchema(
        string resourceName,
        bool isDescriptor,
        bool allowIdentityUpdates,
        JsonArray identityJsonPaths,
        JsonObject documentPathsMapping,
        JsonArray equalityConstraints,
        JsonObject jsonSchemaForInsert,
        bool isResourceExtension = false
    )
    {
        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = isDescriptor,
            ["isSchoolYearEnumeration"] = false,
            ["isResourceExtension"] = isResourceExtension,
            ["allowIdentityUpdates"] = allowIdentityUpdates,
            ["isSubclass"] = false,
            ["identityJsonPaths"] = identityJsonPaths,
            ["booleanJsonPaths"] = new JsonArray(),
            ["numericJsonPaths"] = new JsonArray(),
            ["dateJsonPaths"] = new JsonArray(),
            ["dateTimeJsonPaths"] = new JsonArray(),
            ["equalityConstraints"] = equalityConstraints,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["documentPathsMapping"] = documentPathsMapping,
            ["queryFieldMapping"] = new JsonObject(),
            ["securableElements"] = CreateEmptySecurableElements(),
            ["authorizationPathways"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject CreateScalarPathMapping(string path, bool isPartOfIdentity, bool isRequired)
    {
        return new JsonObject
        {
            ["isReference"] = false,
            ["isPartOfIdentity"] = isPartOfIdentity,
            ["isRequired"] = isRequired,
            ["path"] = path,
        };
    }

    private static JsonObject BuildExtensionBearingStartupCoreProjectSchema()
    {
        return CreateProjectSchema(new JsonObject { ["contacts"] = BuildStartupContactSchema() });
    }

    private static JsonObject BuildExtensionBearingStartupExtensionProjectSchema(
        bool includeUnsupportedRootTableOverride = false
    )
    {
        return CreateProjectSchema(
            new JsonObject
            {
                ["contacts"] = BuildStartupContactExtensionSchema(includeUnsupportedRootTableOverride),
            },
            projectName: "Sample",
            projectEndpointName: "sample",
            isExtensionProject: true
        );
    }

    private static JsonObject BuildStartupContactSchema()
    {
        return CreateCommonResourceSchema(
            resourceName: "Contact",
            isDescriptor: false,
            allowIdentityUpdates: true,
            identityJsonPaths: new JsonArray("$.contactUniqueId"),
            documentPathsMapping: new JsonObject
            {
                ["ContactUniqueId"] = CreateScalarPathMapping(
                    path: "$.contactUniqueId",
                    isPartOfIdentity: true,
                    isRequired: true
                ),
            },
            equalityConstraints: new JsonArray(),
            jsonSchemaForInsert: new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["contactUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                },
                ["required"] = new JsonArray("contactUniqueId"),
            }
        );
    }

    private static JsonObject BuildStartupContactExtensionSchema(bool includeUnsupportedRootTableOverride)
    {
        var resourceSchema = CreateCommonResourceSchema(
            resourceName: "Contact",
            isDescriptor: false,
            allowIdentityUpdates: false,
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: new JsonObject(),
            equalityConstraints: new JsonArray(),
            jsonSchemaForInsert: new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["nickname"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                                },
                            },
                        },
                    },
                },
            },
            isResourceExtension: true
        );

        if (includeUnsupportedRootTableOverride)
        {
            resourceSchema["relational"] = new JsonObject
            {
                ["rootTableNameOverride"] = "ContactExtensionOverride",
            };
        }

        return resourceSchema;
    }

    private static JsonObject CreateEmptySecurableElements()
    {
        return new JsonObject
        {
            ["Namespace"] = new JsonArray(),
            ["EducationOrganization"] = new JsonArray(),
            ["Student"] = new JsonArray(),
            ["Contact"] = new JsonArray(),
            ["Staff"] = new JsonArray(),
        };
    }

    [TestFixture]
    public class Given_Dms_Startup_Runs_With_Postgresql_Runtime_Mapping_Initialization_And_Startup_Validation_Disabled
        : PostgresqlRuntimeMappingInitializationTests
    {
        private const string ConnectionString =
            "Host=localhost;Database=startup-instance-with-validation-disabled;Username=test;Password=test";

        private ServiceProvider _serviceProvider = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;
        private IPostgresqlRuntimeDatabaseMetadataReader _databaseMetadataReader = null!;
        private CapturingLogger<PostgresqlBackendMappingInitializer> _initializerLogger = null!;

        [SetUp]
        public async Task Setup()
        {
            _schemaNodes = CreateSchemaNodes();
            var dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            _databaseMetadataReader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
            _initializerLogger = new CapturingLogger<PostgresqlBackendMappingInitializer>();
            _serviceProvider = CreateStartupServiceProvider(
                _schemaNodes,
                dmsInstanceProvider,
                _databaseMetadataReader,
                CreateAppSettings(validateProvisionedMappingsOnStartup: false),
                _initializerLogger
            );

            A.CallTo(() => dmsInstanceProvider.GetLoadedTenantKeys()).Returns([""]);
            A.CallTo(() => dmsInstanceProvider.GetAll(null))
                .Returns([
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "test",
                        InstanceName: "StartupInstanceValidationDisabled",
                        ConnectionString: ConnectionString,
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
                    ),
                ]);

            await RunStartupInitializationPhasesAsync(_serviceProvider, CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_compiles_and_caches_the_runtime_mapping_set_without_validating_loaded_instances()
        {
            var mappingSetKey = CreateMappingSetKey(BuildEffectiveSchemaSet(_schemaNodes));

            var cache = _serviceProvider.GetRequiredService<MappingSetCache>();
            var cacheResult = await cache.GetOrCreateWithCacheStatusAsync(
                mappingSetKey,
                CancellationToken.None
            );

            cacheResult.CacheStatus.Should().Be(MappingSetCacheStatus.ReusedCompleted);
            cacheResult.MappingSet.Key.Should().Be(mappingSetKey);
            _serviceProvider
                .GetRequiredService<PostgresqlValidatedResourceKeyMapCache>()
                .TryGet(ConnectionString, out _)
                .Should()
                .BeFalse();
            A.CallTo(() =>
                    _databaseMetadataReader.ReadFingerprintAsync(
                        ConnectionString,
                        A<CancellationToken>.Ignored
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _databaseMetadataReader.ReadResourceKeysAsync(
                        ConnectionString,
                        A<CancellationToken>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }

        [Test]
        public void It_logs_mapping_set_compilation_at_information_level()
        {
            _initializerLogger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message.Contains(
                        "Compiled PostgreSQL runtime mapping set",
                        StringComparison.Ordinal
                    )
                );
        }
    }

    [TestFixture]
    public class Given_Dms_Startup_Runs_With_Postgresql_Runtime_Mapping_Initialization
        : PostgresqlRuntimeMappingInitializationTests
    {
        private ServiceProvider _serviceProvider = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;
        private IDmsInstanceProvider _dmsInstanceProvider = null!;
        private IPostgresqlRuntimeDatabaseMetadataReader _databaseMetadataReader = null!;
        private const string ConnectionString =
            "Host=localhost;Database=startup-instance;Username=test;Password=test";

        [SetUp]
        public void Setup()
        {
            _schemaNodes = CreateSchemaNodes();
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            _databaseMetadataReader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
            _serviceProvider = CreateStartupServiceProvider(
                _schemaNodes,
                _dmsInstanceProvider,
                _databaseMetadataReader,
                CreateAppSettings(validateProvisionedMappingsOnStartup: true)
            );
            var effectiveSchemaSet = BuildEffectiveSchemaSet(_schemaNodes);

            A.CallTo(() => _dmsInstanceProvider.GetLoadedTenantKeys()).Returns([""]);
            A.CallTo(() => _dmsInstanceProvider.GetAll(null))
                .Returns([
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "test",
                        InstanceName: "StartupInstance",
                        ConnectionString: ConnectionString,
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
                    ),
                ]);
            A.CallTo(() =>
                    _databaseMetadataReader.ReadFingerprintAsync(
                        ConnectionString,
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns(
                    new PostgresqlDatabaseFingerprintReadResult.Success(
                        CreateMatchingFingerprint(effectiveSchemaSet)
                    )
                );
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_primes_the_runtime_mapping_cache_before_requests_begin()
        {
            await RunStartupInitializationPhasesAsync(_serviceProvider, CancellationToken.None);

            var mappingSetKey = CreateMappingSetKey(BuildEffectiveSchemaSet(_schemaNodes));

            var cache = _serviceProvider.GetRequiredService<MappingSetCache>();
            var cacheResult = await cache.GetOrCreateWithCacheStatusAsync(
                mappingSetKey,
                CancellationToken.None
            );

            cacheResult.CacheStatus.Should().Be(MappingSetCacheStatus.ReusedCompleted);
            cacheResult.MappingSet.Key.Should().Be(mappingSetKey);
        }
    }

    [TestFixture]
    public class Given_Dms_Startup_Runs_With_Grouped_Reference_Runtime_Mapping_Initialization
        : PostgresqlRuntimeMappingInitializationTests
    {
        private const string ConnectionString =
            "Host=localhost;Database=startup-grouped-reference-instance;Username=test;Password=test";

        private ServiceProvider _serviceProvider = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;
        private IDmsInstanceProvider _dmsInstanceProvider = null!;
        private IPostgresqlRuntimeDatabaseMetadataReader _databaseMetadataReader = null!;

        private static JsonObject CreateReferenceMapping(
            string resourceName,
            params (string IdentityJsonPath, string ReferenceJsonPath)[] referenceJsonPaths
        )
        {
            return new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isRequired"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = resourceName,
                ["referenceJsonPaths"] = new JsonArray(
                    referenceJsonPaths
                        .Select(path =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["identityJsonPath"] = path.IdentityJsonPath,
                                    ["referenceJsonPath"] = path.ReferenceJsonPath,
                                }
                        )
                        .ToArray()
                ),
            };
        }

        private static JsonObject BuildGroupedReferenceStartupProjectSchema()
        {
            return CreateProjectSchema(
                new JsonObject
                {
                    ["enrollments"] = BuildGroupedReferenceStartupEnrollmentSchema(),
                    ["schools"] = BuildGroupedReferenceStartupSchoolSchema(),
                }
            );
        }

        private static JsonObject BuildGroupedReferenceStartupEnrollmentSchema()
        {
            return CreateCommonResourceSchema(
                resourceName: "Enrollment",
                isDescriptor: false,
                allowIdentityUpdates: false,
                identityJsonPaths: new JsonArray("$.enrollmentCode"),
                documentPathsMapping: new JsonObject
                {
                    ["EnrollmentCode"] = CreateScalarPathMapping(
                        path: "$.enrollmentCode",
                        isPartOfIdentity: true,
                        isRequired: true
                    ),
                    ["School"] = CreateReferenceMapping(
                        "School",
                        ("$.schoolId", "$.schoolReference.schoolId"),
                        ("$.localEducationAgencyId", "$.schoolReference.schoolId")
                    ),
                },
                equalityConstraints: new JsonArray(),
                jsonSchemaForInsert: new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["enrollmentCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                        ["schoolReference"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["schoolId"] = new JsonObject { ["type"] = "integer" },
                            },
                        },
                    },
                    ["required"] = new JsonArray("enrollmentCode"),
                }
            );
        }

        private static JsonObject BuildGroupedReferenceStartupSchoolSchema()
        {
            return CreateCommonResourceSchema(
                resourceName: "School",
                isDescriptor: false,
                allowIdentityUpdates: true,
                identityJsonPaths: new JsonArray("$.schoolId", "$.localEducationAgencyId"),
                documentPathsMapping: new JsonObject
                {
                    ["SchoolId"] = CreateScalarPathMapping(
                        path: "$.schoolId",
                        isPartOfIdentity: true,
                        isRequired: true
                    ),
                    ["LocalEducationAgencyId"] = CreateScalarPathMapping(
                        path: "$.localEducationAgencyId",
                        isPartOfIdentity: true,
                        isRequired: true
                    ),
                },
                equalityConstraints: new JsonArray(
                    new JsonObject
                    {
                        ["sourceJsonPath"] = "$.schoolId",
                        ["targetJsonPath"] = "$.localEducationAgencyId",
                    }
                ),
                jsonSchemaForInsert: new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        ["localEducationAgencyId"] = new JsonObject { ["type"] = "integer" },
                    },
                    ["required"] = new JsonArray("schoolId", "localEducationAgencyId"),
                }
            );
        }

        [SetUp]
        public void Setup()
        {
            _schemaNodes = CreateSchemaNodes(BuildGroupedReferenceStartupProjectSchema());
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            _databaseMetadataReader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
            _serviceProvider = CreateStartupServiceProvider(
                _schemaNodes,
                _dmsInstanceProvider,
                _databaseMetadataReader,
                CreateAppSettings(validateProvisionedMappingsOnStartup: true)
            );

            var effectiveSchemaSet = BuildEffectiveSchemaSet(_schemaNodes);

            A.CallTo(() => _dmsInstanceProvider.GetLoadedTenantKeys()).Returns([""]);
            A.CallTo(() => _dmsInstanceProvider.GetAll(null))
                .Returns([
                    new DmsInstance(
                        Id: 2,
                        InstanceType: "test",
                        InstanceName: "GroupedReferenceStartupInstance",
                        ConnectionString: ConnectionString,
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
                    ),
                ]);
            A.CallTo(() =>
                    _databaseMetadataReader.ReadFingerprintAsync(
                        ConnectionString,
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns(
                    new PostgresqlDatabaseFingerprintReadResult.Success(
                        CreateMatchingFingerprint(effectiveSchemaSet)
                    )
                );
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_primes_the_runtime_mapping_cache_for_grouped_reference_schemas()
        {
            await RunStartupInitializationPhasesAsync(_serviceProvider, CancellationToken.None);

            var mappingSetKey = CreateMappingSetKey(BuildEffectiveSchemaSet(_schemaNodes));
            var cache = _serviceProvider.GetRequiredService<MappingSetCache>();
            var cacheResult = await cache.GetOrCreateWithCacheStatusAsync(
                mappingSetKey,
                CancellationToken.None
            );

            cacheResult.CacheStatus.Should().Be(MappingSetCacheStatus.ReusedCompleted);
            cacheResult.MappingSet.Key.Should().Be(mappingSetKey);
            cacheResult
                .MappingSet.ReadPlansByResource.Should()
                .ContainKey(new QualifiedResourceName("Ed-Fi", "Enrollment"));

            var schoolReferenceBinding = cacheResult
                .MappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "Enrollment")]
                .ReferenceIdentityProjectionPlansInDependencyOrder.Should()
                .ContainSingle()
                .Subject.BindingsInOrder.Should()
                .ContainSingle()
                .Subject;

            schoolReferenceBinding.ReferenceObjectPath.Canonical.Should().Be("$.schoolReference");
            schoolReferenceBinding
                .IdentityFieldOrdinalsInOrder.Select(static field => field.ReferenceJsonPath.Canonical)
                .Should()
                .Equal("$.schoolReference.schoolId");
        }
    }

    [TestFixture]
    public class Given_Dms_Startup_Runs_With_Extension_Bearing_Postgresql_Runtime_Mapping_Initialization
        : PostgresqlRuntimeMappingInitializationTests
    {
        private const string ConnectionString =
            "Host=localhost;Database=startup-extension-instance;Username=test;Password=test";

        private ServiceProvider _serviceProvider = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;
        private IDmsInstanceProvider _dmsInstanceProvider = null!;
        private IPostgresqlRuntimeDatabaseMetadataReader _databaseMetadataReader = null!;

        [SetUp]
        public void Setup()
        {
            _schemaNodes = CreateSchemaNodes(
                BuildExtensionBearingStartupCoreProjectSchema(),
                BuildExtensionBearingStartupExtensionProjectSchema()
            );
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            _databaseMetadataReader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
            _serviceProvider = CreateStartupServiceProvider(
                _schemaNodes,
                _dmsInstanceProvider,
                _databaseMetadataReader,
                CreateAppSettings(validateProvisionedMappingsOnStartup: true)
            );

            var effectiveSchemaSet = BuildEffectiveSchemaSet(_schemaNodes);

            A.CallTo(() => _dmsInstanceProvider.GetLoadedTenantKeys()).Returns([""]);
            A.CallTo(() => _dmsInstanceProvider.GetAll(null))
                .Returns([
                    new DmsInstance(
                        Id: 3,
                        InstanceType: "test",
                        InstanceName: "ExtensionStartupInstance",
                        ConnectionString: ConnectionString,
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
                    ),
                ]);
            A.CallTo(() =>
                    _databaseMetadataReader.ReadFingerprintAsync(
                        ConnectionString,
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns(
                    new PostgresqlDatabaseFingerprintReadResult.Success(
                        CreateMatchingFingerprint(effectiveSchemaSet)
                    )
                );
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_primes_the_runtime_mapping_cache_for_extension_bearing_schemas()
        {
            await RunStartupInitializationPhasesAsync(_serviceProvider, CancellationToken.None);

            var mappingSetKey = CreateMappingSetKey(BuildEffectiveSchemaSet(_schemaNodes));
            var cache = _serviceProvider.GetRequiredService<MappingSetCache>();
            var cacheResult = await cache.GetOrCreateWithCacheStatusAsync(
                mappingSetKey,
                CancellationToken.None
            );

            cacheResult.CacheStatus.Should().Be(MappingSetCacheStatus.ReusedCompleted);
            cacheResult.MappingSet.Key.Should().Be(mappingSetKey);

            var contactReadPlan = cacheResult.MappingSet.ReadPlansByResource[
                new QualifiedResourceName("Ed-Fi", "Contact")
            ];
            var extensionTablePlan = contactReadPlan
                .TablePlansInDependencyOrder.Should()
                .ContainSingle(tablePlan =>
                    tablePlan.TableModel.Table.Schema.Value == "sample"
                    && tablePlan.TableModel.Table.Name == "ContactExtension"
                )
                .Which;

            extensionTablePlan.TableModel.JsonScope.Canonical.Should().Be("$._ext.sample");
        }
    }

    [TestFixture]
    public class Given_Dms_Startup_Runs_With_Invalid_Extension_Bearing_Postgresql_Runtime_Mapping_Initialization
        : PostgresqlRuntimeMappingInitializationTests
    {
        private const string ConnectionString =
            "Host=localhost;Database=startup-invalid-extension-instance;Username=test;Password=test";

        private ServiceProvider _serviceProvider = null!;
        private ApiSchemaDocumentNodes _schemaNodes = null!;
        private IDmsInstanceProvider _dmsInstanceProvider = null!;
        private IPostgresqlRuntimeDatabaseMetadataReader _databaseMetadataReader = null!;

        [SetUp]
        public void Setup()
        {
            _schemaNodes = CreateSchemaNodes(
                BuildExtensionBearingStartupCoreProjectSchema(),
                BuildExtensionBearingStartupExtensionProjectSchema(includeUnsupportedRootTableOverride: true)
            );
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            _databaseMetadataReader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
            _serviceProvider = CreateStartupServiceProvider(
                _schemaNodes,
                _dmsInstanceProvider,
                _databaseMetadataReader
            );

            A.CallTo(() => _dmsInstanceProvider.GetLoadedTenantKeys()).Returns([""]);
            A.CallTo(() => _dmsInstanceProvider.GetAll(null))
                .Returns([
                    new DmsInstance(
                        Id: 4,
                        InstanceType: "test",
                        InstanceName: "InvalidExtensionStartupInstance",
                        ConnectionString: ConnectionString,
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
                    ),
                ]);
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_surfaces_the_extension_compile_failure_details()
        {
            var orchestrator = _serviceProvider.GetRequiredService<DmsStartupOrchestrator>();

            await orchestrator.RunByOrderRangeAsync(
                0,
                DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum,
                CancellationToken.None
            );

            Func<Task> act = () =>
                orchestrator.RunByOrderRangeAsync(
                    DmsStartupTaskOrderRanges.BackendMappingMinimum,
                    DmsStartupTaskOrderRanges.BackendMappingMaximum,
                    CancellationToken.None
                );

            var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;

            exception.Message.Should().Contain("Startup task 'Backend Mapping Initialization' failed");
            exception.Message.Should().Contain("rootTableNameOverride");
            exception.Message.Should().Contain("Sample:Contact");
            exception.InnerException.Should().NotBeNull();
            exception.InnerException!.Message.Should().Contain("rootTableNameOverride");
            exception.InnerException.Message.Should().Contain("Sample:Contact");
        }
    }

    [TestFixture]
    public class Given_The_Raw_Api_Schema_Source_Changes_After_Api_Initialization
        : PostgresqlRuntimeMappingInitializationTests
    {
        private const string ConnectionString =
            "Host=localhost;Database=startup-shared-effective-schema-instance;Username=test;Password=test";

        private ServiceProvider _serviceProvider = null!;
        private ApiSchemaDocumentNodes _startupSchemaNodes = null!;
        private IApiSchemaProvider _apiSchemaProvider = null!;
        private IDmsInstanceProvider _dmsInstanceProvider = null!;
        private IPostgresqlRuntimeDatabaseMetadataReader _databaseMetadataReader = null!;

        [SetUp]
        public void Setup()
        {
            _startupSchemaNodes = CreateSchemaNodes();
            _apiSchemaProvider = A.Fake<IApiSchemaProvider>();
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            _databaseMetadataReader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
            _serviceProvider = CreateStartupServiceProvider(
                _apiSchemaProvider,
                _dmsInstanceProvider,
                _databaseMetadataReader,
                CreateAppSettings(validateProvisionedMappingsOnStartup: true)
            );

            A.CallTo(() => _apiSchemaProvider.GetApiSchemaNodes()).Returns(_startupSchemaNodes);
            A.CallTo(() => _apiSchemaProvider.IsSchemaValid).Returns(true);
            A.CallTo(() => _apiSchemaProvider.ApiSchemaFailures).Returns([]);

            var effectiveSchemaSet = BuildEffectiveSchemaSet(_startupSchemaNodes);

            A.CallTo(() => _dmsInstanceProvider.GetLoadedTenantKeys()).Returns([""]);
            A.CallTo(() => _dmsInstanceProvider.GetAll(null))
                .Returns([
                    new DmsInstance(
                        Id: 5,
                        InstanceType: "test",
                        InstanceName: "SharedEffectiveSchemaStartupInstance",
                        ConnectionString: ConnectionString,
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
                    ),
                ]);
            A.CallTo(() =>
                    _databaseMetadataReader.ReadFingerprintAsync(
                        ConnectionString,
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns(
                    new PostgresqlDatabaseFingerprintReadResult.Success(
                        CreateMatchingFingerprint(effectiveSchemaSet)
                    )
                );
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_reuses_the_authoritative_effective_schema_set_from_api_schema_startup()
        {
            var orchestrator = _serviceProvider.GetRequiredService<DmsStartupOrchestrator>();

            await orchestrator.RunByOrderRangeAsync(
                0,
                DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum,
                CancellationToken.None
            );

            var changedSchemaNodes = CreateSchemaNodes(
                BuildExtensionBearingStartupCoreProjectSchema(),
                BuildExtensionBearingStartupExtensionProjectSchema(includeUnsupportedRootTableOverride: true)
            );

            A.CallTo(() => _apiSchemaProvider.GetApiSchemaNodes()).Returns(changedSchemaNodes);

            await orchestrator.RunByOrderRangeAsync(
                DmsStartupTaskOrderRanges.BackendMappingMinimum,
                DmsStartupTaskOrderRanges.BackendMappingMaximum,
                CancellationToken.None
            );

            var mappingSetKey = CreateMappingSetKey(BuildEffectiveSchemaSet(_startupSchemaNodes));
            var cache = _serviceProvider.GetRequiredService<MappingSetCache>();
            var cacheResult = await cache.GetOrCreateWithCacheStatusAsync(
                mappingSetKey,
                CancellationToken.None
            );

            cacheResult.CacheStatus.Should().Be(MappingSetCacheStatus.ReusedCompleted);
            cacheResult.MappingSet.Key.Should().Be(mappingSetKey);
            A.CallTo(() => _apiSchemaProvider.GetApiSchemaNodes()).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Runs_Without_Api_Schema_Initialization
        : PostgresqlRuntimeMappingInitializationTests
    {
        private ServiceProvider _serviceProvider = null!;

        [SetUp]
        public void Setup()
        {
            _serviceProvider = CreateStartupServiceProvider(
                CreateSchemaNodes(),
                A.Fake<IDmsInstanceProvider>(),
                A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>()
            );
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public async Task It_fails_fast_with_an_actionable_missing_startup_state_message()
        {
            var orchestrator = _serviceProvider.GetRequiredService<DmsStartupOrchestrator>();

            Func<Task> act = () =>
                orchestrator.RunByOrderRangeAsync(
                    DmsStartupTaskOrderRanges.BackendMappingMinimum,
                    DmsStartupTaskOrderRanges.BackendMappingMaximum,
                    CancellationToken.None
                );

            var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;

            exception.Message.Should().Contain("Startup task 'Backend Mapping Initialization' failed");
            exception.InnerException.Should().NotBeNull();
            exception
                .InnerException!.Message.Should()
                .Contain("authoritative effective schema startup state is unavailable");
            exception.InnerException.InnerException.Should().NotBeNull();
            exception
                .InnerException.InnerException!.Message.Should()
                .Contain("EffectiveSchemaSetProvider has not been initialized");
        }
    }

    [TestFixture]
    public class Given_Postgresql_Runtime_Mapping_Compilation_When_The_Authoritative_Effective_Schema_Key_Changes
        : PostgresqlRuntimeMappingInitializationTests
    {
        private Func<Task> _act = null!;
        private MappingSetKey _expectedKey;
        private MappingSetKey _actualKey;

        private static string FormatMappingSetKey(MappingSetKey key)
        {
            return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
        }

        [SetUp]
        public void Setup()
        {
            var initialEffectiveSchemaSet = BuildEffectiveSchemaSet(CreateSchemaNodes());
            var changedEffectiveSchemaSet = BuildEffectiveSchemaSet(
                CreateSchemaNodes(
                    BuildExtensionBearingStartupCoreProjectSchema(),
                    BuildExtensionBearingStartupExtensionProjectSchema()
                )
            );
            var effectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            var compiler = new PostgresqlRuntimeMappingSetCompiler(
                effectiveSchemaSetProvider,
                new MappingSetCompiler()
            );
            var accessCount = 0;

            A.CallTo(() => effectiveSchemaSetProvider.EffectiveSchemaSet)
                .ReturnsLazily(() =>
                    accessCount++ switch
                    {
                        0 => initialEffectiveSchemaSet,
                        _ => changedEffectiveSchemaSet,
                    }
                );

            _expectedKey = compiler.GetCurrentKey();
            _actualKey = CreateMappingSetKey(changedEffectiveSchemaSet);
            _act = async () => await compiler.CompileAsync(_expectedKey);
        }

        [Test]
        public async Task It_throws_with_the_expected_and_actual_mapping_set_keys()
        {
            var exception = (await _act.Should().ThrowAsync<InvalidOperationException>()).Which;

            exception.Message.Should().Contain(FormatMappingSetKey(_expectedKey));
            exception.Message.Should().Contain(FormatMappingSetKey(_actualKey));
            exception.Message.Should().Contain("current schema resolved");
        }
    }

    [TestFixture]
    public class Given_Postgresql_Runtime_Mapping_Compilation_With_An_Arbitrary_Duplicate_Scalar_Path
        : PostgresqlRuntimeMappingInitializationTests
    {
        private Action _act = null!;

        private static EffectiveSchemaSet CloneEffectiveSchemaSet(EffectiveSchemaSet original)
        {
            var clonedProjects = original
                .ProjectsInEndpointOrder.Select(project => new EffectiveProjectSchema(
                    project.ProjectEndpointName,
                    project.ProjectName,
                    project.ProjectVersion,
                    project.IsExtensionProject,
                    (JsonObject)project.ProjectSchema.DeepClone()
                ))
                .ToArray();

            return new EffectiveSchemaSet(original.EffectiveSchema, clonedProjects);
        }

        private static MappingSet CompileRuntimeMappingSet(
            ApiSchemaDocumentNodes schemaNodes,
            IReadOnlyList<IRelationalModelSetPass> passes
        )
        {
            var effectiveSchemaSet = CloneEffectiveSchemaSet(BuildEffectiveSchemaSet(schemaNodes));
            var derivedModelSet = new DerivedRelationalModelSetBuilder(passes).Build(
                effectiveSchemaSet,
                SqlDialect.Pgsql,
                new PgsqlDialectRules()
            );

            return new MappingSetCompiler().Compile(derivedModelSet);
        }

        private static JsonObject BuildAmbiguousEndpointProjectSchema()
        {
            return CreateProjectSchema(
                new JsonObject { ["ambiguousExamples"] = BuildAmbiguousEndpointResourceSchema() }
            );
        }

        private static JsonObject BuildAmbiguousEndpointResourceSchema()
        {
            return CreateCommonResourceSchema(
                resourceName: "AmbiguousExample",
                isDescriptor: false,
                allowIdentityUpdates: false,
                identityJsonPaths: new JsonArray(),
                documentPathsMapping: new JsonObject(),
                equalityConstraints: new JsonArray(
                    new JsonObject
                    {
                        ["sourceJsonPath"] = "$.fiscalYear",
                        ["targetJsonPath"] = "$.localFiscalYear",
                    }
                ),
                jsonSchemaForInsert: new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["fiscalYear"] = new JsonObject { ["type"] = "integer" },
                        ["localFiscalYear"] = new JsonObject { ["type"] = "integer" },
                    },
                }
            );
        }

        [SetUp]
        public void Setup()
        {
            var schemaNodes = CreateSchemaNodes(BuildAmbiguousEndpointProjectSchema());
            var passes = RelationalModelSetPasses.CreateDefault().ToList();
            var keyUnificationPassIndex = passes.FindIndex(static pass => pass is KeyUnificationPass);

            if (keyUnificationPassIndex < 0)
            {
                throw new InvalidOperationException(
                    "Default relational-model passes do not include KeyUnificationPass."
                );
            }

            passes.Insert(
                keyUnificationPassIndex,
                new DuplicateSourcePathBindingPass(
                    resourceName: "AmbiguousExample",
                    sourcePath: "$.fiscalYear",
                    aliasSuffix: "Alias"
                )
            );

            _act = () => _ = CompileRuntimeMappingSet(schemaNodes, passes);
        }

        [Test]
        public void It_still_fails_fast_during_runtime_mapping_compilation()
        {
            _act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*resolved to multiple distinct bindings*");
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

file sealed class DuplicateSourcePathBindingPass(string resourceName, string sourcePath, string aliasSuffix)
    : IRelationalModelSetPass
{
    public void Execute(RelationalModelSetBuilderContext context)
    {
        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[index];

            if (
                !string.Equals(
                    concreteResource.ResourceKey.Resource.ResourceName,
                    resourceName,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            var updatedTables = concreteResource
                .RelationalModel.TablesInDependencyOrder.Select(DuplicateSourcePathColumn)
                .ToArray();
            var updatedRoot = updatedTables.Single(table =>
                table.Table.Equals(concreteResource.RelationalModel.Root.Table)
            );
            var updatedModel = concreteResource.RelationalModel with
            {
                Root = updatedRoot,
                TablesInDependencyOrder = updatedTables,
            };

            context.ConcreteResourcesInNameOrder[index] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    private DbTableModel DuplicateSourcePathColumn(DbTableModel table)
    {
        var sourceColumn = table.Columns.SingleOrDefault(column =>
            column.SourceJsonPath?.Canonical == sourcePath
        );

        if (sourceColumn is null)
        {
            return table;
        }

        var duplicateName = AllocateDuplicateName(table.Columns, sourceColumn.ColumnName, aliasSuffix);
        var duplicateColumn = sourceColumn with { ColumnName = duplicateName };

        return table with
        {
            Columns = table.Columns.Concat([duplicateColumn]).ToArray(),
        };
    }

    private static DbColumnName AllocateDuplicateName(
        IReadOnlyList<DbColumnModel> existingColumns,
        DbColumnName sourceColumnName,
        string aliasSuffix
    )
    {
        var existingNames = existingColumns
            .Select(column => column.ColumnName.Value)
            .ToHashSet(StringComparer.Ordinal);
        var initialName = $"{sourceColumnName.Value}_{aliasSuffix}";

        if (existingNames.Add(initialName))
        {
            return new DbColumnName(initialName);
        }

        var suffix = 2;

        while (true)
        {
            var candidate = $"{sourceColumnName.Value}_{aliasSuffix}_{suffix}";

            if (existingNames.Add(candidate))
            {
                return new DbColumnName(candidate);
            }

            suffix++;
        }
    }
}
