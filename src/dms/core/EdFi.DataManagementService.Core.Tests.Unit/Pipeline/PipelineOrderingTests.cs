// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit.Pipeline;

[TestFixture]
[Parallelizable]
public class PipelineOrderingTests
{
    private static List<Type> GetStepTypes(ApiService apiService, string factoryMethodName)
    {
        var method = typeof(ApiService).GetMethod(
            factoryMethodName,
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        method.Should().NotBeNull($"{factoryMethodName} should exist on ApiService");

        var pipeline = (PipelineProvider)method!.Invoke(apiService, null)!;
        var field = typeof(PipelineProvider)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .SingleOrDefault(info => info.FieldType == typeof(List<IPipelineStep>));

        field.Should().NotBeNull("PipelineProvider should store its steps for execution");

        var steps = (List<IPipelineStep>)field!.GetValue(pipeline)!;
        return steps.Select(step => step.GetType()).ToList();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Query_Pipeline : PipelineOrderingTests
    {
        private List<Type> _stepTypes = [];

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();

            services.Configure<JwtAuthenticationOptions>(options => { });
            services.AddTransient<JwtAuthenticationMiddleware>();
            services.AddTransient<IJwtValidationService>(_ => A.Fake<IJwtValidationService>());
            services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
                NullLogger<JwtAuthenticationMiddleware>.Instance
            );

            services.AddTransient<ResolveDataStoreMiddleware>();
            services.AddSingleton<IApplicationContextProvider>(A.Fake<IApplicationContextProvider>());
            services.AddSingleton<IDataStoreProvider>(A.Fake<IDataStoreProvider>());
            services.AddSingleton<IDataStoreSelection>(A.Fake<IDataStoreSelection>());
            services.AddTransient<ILogger<ResolveDataStoreMiddleware>>(_ =>
                NullLogger<ResolveDataStoreMiddleware>.Instance
            );

            var appSettingsOptions = Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false }
            );
            services.AddSingleton(appSettingsOptions);
            services.AddSingleton<IDatabaseFingerprintReader, NullDatabaseFingerprintReader>();
            services.AddSingleton<DatabaseFingerprintProvider>();
            services.AddTransient<ValidateDatabaseFingerprintMiddleware>();
            services.AddTransient<ILogger<ValidateDatabaseFingerprintMiddleware>>(_ =>
                NullLogger<ValidateDatabaseFingerprintMiddleware>.Instance
            );

            TestHelper.AddResourceKeyValidationServices(services);

            TestHelper.AddMappingSetResolutionServices(services);

            services.AddSingleton<IProfileService>(A.Fake<IProfileService>());
            services.AddTransient<ProfileResolutionMiddleware>();
            services.AddTransient<ILogger<ProfileResolutionMiddleware>>(_ =>
                NullLogger<ProfileResolutionMiddleware>.Instance
            );

            var serviceProvider = services.BuildServiceProvider();

            var apiService = new ApiService(
                A.Fake<IApiSchemaProvider>(),
                A.Fake<IEffectiveApiSchemaProvider>(),
                A.Fake<IClaimSetProvider>(),
                A.Fake<IDocumentValidator>(),
                A.Fake<IMatchingDocumentUuidsValidator>(),
                A.Fake<IEqualityConstraintValidator>(),
                A.Fake<IDecimalValidator>(),
                NullLogger<ApiService>.Instance,
                NullLoggerFactory.Instance,
                appSettingsOptions,
                ResiliencePipeline.Empty,
                A.Fake<ResourceLoadOrderCalculator>(),
                serviceProvider,
                A.Fake<IServiceScopeFactory>(),
                A.Fake<CachedClaimSetProvider>(),
                A.Fake<IResourceDependencyGraphMLFactory>(),
                A.Fake<IProfileService>()
            );

            _stepTypes = GetStepTypes(apiService, "CreateQueryPipeline");
        }

        [Test]
        public void It_contains_ValidateDatabaseFingerprintMiddleware()
        {
            _stepTypes.Should().Contain(typeof(ValidateDatabaseFingerprintMiddleware));
        }

        [Test]
        public void It_places_parse_path_after_resolve_data_store()
        {
            var resolveIndex = _stepTypes.IndexOf(typeof(ResolveDataStoreMiddleware));
            var parsePathIndex = _stepTypes.IndexOf(typeof(ParsePathMiddleware));

            resolveIndex.Should().BeGreaterThanOrEqualTo(0);
            parsePathIndex
                .Should()
                .BeGreaterThan(
                    resolveIndex,
                    "ParsePathMiddleware must come after ResolveDataStoreMiddleware"
                );
        }

        [Test]
        public void It_places_fingerprint_validation_after_parse_path()
        {
            var parsePathIndex = _stepTypes.IndexOf(typeof(ParsePathMiddleware));
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));

            parsePathIndex.Should().BeGreaterThanOrEqualTo(0);
            fingerprintIndex
                .Should()
                .BeGreaterThan(
                    parsePathIndex,
                    "ValidateDatabaseFingerprintMiddleware must come after ParsePathMiddleware"
                );
        }

        [Test]
        public void It_places_fingerprint_validation_before_the_first_schema_dependent_step()
        {
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));
            var apiSchemaValidationIndex = _stepTypes.IndexOf(typeof(ApiSchemaValidationMiddleware));

            fingerprintIndex.Should().BeGreaterThanOrEqualTo(0);
            apiSchemaValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            fingerprintIndex
                .Should()
                .BeLessThan(
                    apiSchemaValidationIndex,
                    "ValidateDatabaseFingerprintMiddleware must run before schema-dependent middleware"
                );
        }

        [Test]
        public void It_contains_ValidateResourceKeySeedMiddleware()
        {
            _stepTypes.Should().Contain(typeof(ValidateResourceKeySeedMiddleware));
        }

        [Test]
        public void It_places_resource_key_validation_after_fingerprint_validation()
        {
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));

            fingerprintIndex.Should().BeGreaterThanOrEqualTo(0);
            resourceKeyIndex
                .Should()
                .BeGreaterThan(
                    fingerprintIndex,
                    "ValidateResourceKeySeedMiddleware must come after ValidateDatabaseFingerprintMiddleware"
                );
        }

        [Test]
        public void It_places_resource_key_validation_before_the_first_schema_dependent_step()
        {
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));
            var apiSchemaValidationIndex = _stepTypes.IndexOf(typeof(ApiSchemaValidationMiddleware));

            resourceKeyIndex.Should().BeGreaterThanOrEqualTo(0);
            apiSchemaValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            resourceKeyIndex
                .Should()
                .BeLessThan(
                    apiSchemaValidationIndex,
                    "ValidateResourceKeySeedMiddleware must run before schema-dependent middleware"
                );
        }

        [Test]
        public void It_contains_ResolveMappingSetMiddleware()
        {
            _stepTypes.Should().Contain(typeof(ResolveMappingSetMiddleware));
        }

        [Test]
        public void It_places_resolve_mapping_set_after_resource_key_validation()
        {
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));
            var mappingSetIndex = _stepTypes.IndexOf(typeof(ResolveMappingSetMiddleware));

            resourceKeyIndex.Should().BeGreaterThanOrEqualTo(0);
            mappingSetIndex
                .Should()
                .BeGreaterThan(
                    resourceKeyIndex,
                    "ResolveMappingSetMiddleware must come after ValidateResourceKeySeedMiddleware"
                );
        }

        [Test]
        public void It_places_resolve_mapping_set_before_the_first_schema_dependent_step()
        {
            var mappingSetIndex = _stepTypes.IndexOf(typeof(ResolveMappingSetMiddleware));
            var apiSchemaValidationIndex = _stepTypes.IndexOf(typeof(ApiSchemaValidationMiddleware));

            mappingSetIndex.Should().BeGreaterThanOrEqualTo(0);
            apiSchemaValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            mappingSetIndex
                .Should()
                .BeLessThan(
                    apiSchemaValidationIndex,
                    "ResolveMappingSetMiddleware must run before schema-dependent middleware"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Tracked_Changes_Pipeline : PipelineOrderingTests
    {
        private List<Type> _stepTypes = [];

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();

            services.Configure<JwtAuthenticationOptions>(options => { });
            services.AddTransient<JwtAuthenticationMiddleware>();
            services.AddTransient<IJwtValidationService>(_ => A.Fake<IJwtValidationService>());
            services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
                NullLogger<JwtAuthenticationMiddleware>.Instance
            );

            services.AddTransient<ResolveDataStoreMiddleware>();
            services.AddSingleton<IApplicationContextProvider>(A.Fake<IApplicationContextProvider>());
            services.AddSingleton<IDataStoreProvider>(A.Fake<IDataStoreProvider>());
            services.AddSingleton<IDataStoreSelection>(A.Fake<IDataStoreSelection>());
            services.AddTransient<ILogger<ResolveDataStoreMiddleware>>(_ =>
                NullLogger<ResolveDataStoreMiddleware>.Instance
            );

            var appSettingsOptions = Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false }
            );
            services.AddSingleton(appSettingsOptions);
            services.AddSingleton<IDatabaseFingerprintReader, NullDatabaseFingerprintReader>();
            services.AddSingleton<DatabaseFingerprintProvider>();
            services.AddTransient<ValidateDatabaseFingerprintMiddleware>();
            services.AddTransient<ILogger<ValidateDatabaseFingerprintMiddleware>>(_ =>
                NullLogger<ValidateDatabaseFingerprintMiddleware>.Instance
            );

            TestHelper.AddResourceKeyValidationServices(services);

            TestHelper.AddMappingSetResolutionServices(services);

            var serviceProvider = services.BuildServiceProvider();

            var apiService = new ApiService(
                A.Fake<IApiSchemaProvider>(),
                A.Fake<IEffectiveApiSchemaProvider>(),
                A.Fake<IClaimSetProvider>(),
                A.Fake<IDocumentValidator>(),
                A.Fake<IMatchingDocumentUuidsValidator>(),
                A.Fake<IEqualityConstraintValidator>(),
                A.Fake<IDecimalValidator>(),
                NullLogger<ApiService>.Instance,
                NullLoggerFactory.Instance,
                appSettingsOptions,
                ResiliencePipeline.Empty,
                A.Fake<ResourceLoadOrderCalculator>(),
                serviceProvider,
                A.Fake<IServiceScopeFactory>(),
                A.Fake<CachedClaimSetProvider>(),
                A.Fake<IResourceDependencyGraphMLFactory>(),
                A.Fake<IProfileService>()
            );

            _stepTypes = GetStepTypes(apiService, "CreateGetTrackedChangesPipeline");
        }

        [Test]
        public void It_places_tracked_change_query_validation_after_query_validation()
        {
            var queryValidationIndex = _stepTypes.IndexOf(typeof(ValidateQueryMiddleware));
            var trackedQueryValidationIndex = _stepTypes.IndexOf(
                typeof(ValidateTrackedChangeQueryMiddleware)
            );

            queryValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            trackedQueryValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            trackedQueryValidationIndex
                .Should()
                .BeGreaterThan(
                    queryValidationIndex,
                    "ValidateTrackedChangeQueryMiddleware must reject parsed resource query filters"
                );
        }

        [Test]
        public void It_contains_ValidateResourceKeySeedMiddleware()
        {
            _stepTypes.Should().Contain(typeof(ValidateResourceKeySeedMiddleware));
        }

        [Test]
        public void It_places_resource_key_validation_after_fingerprint_validation()
        {
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));

            fingerprintIndex.Should().BeGreaterThanOrEqualTo(0);
            resourceKeyIndex
                .Should()
                .BeGreaterThan(
                    fingerprintIndex,
                    "ValidateResourceKeySeedMiddleware must come after ValidateDatabaseFingerprintMiddleware"
                );
        }

        [Test]
        public void It_places_resource_key_validation_before_mapping_set_resolution()
        {
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));
            var mappingSetIndex = _stepTypes.IndexOf(typeof(ResolveMappingSetMiddleware));

            resourceKeyIndex.Should().BeGreaterThanOrEqualTo(0);
            mappingSetIndex.Should().BeGreaterThanOrEqualTo(0);
            resourceKeyIndex
                .Should()
                .BeLessThan(
                    mappingSetIndex,
                    "ValidateResourceKeySeedMiddleware must validate the database seed before mapping-set resolution"
                );
        }

        [Test]
        public void It_places_tracked_change_query_validation_before_the_handler()
        {
            var trackedQueryValidationIndex = _stepTypes.IndexOf(
                typeof(ValidateTrackedChangeQueryMiddleware)
            );
            var handlerIndex = _stepTypes.IndexOf(typeof(TrackedChangeQueryRequestHandler));

            trackedQueryValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            handlerIndex.Should().BeGreaterThanOrEqualTo(0);
            trackedQueryValidationIndex
                .Should()
                .BeLessThan(
                    handlerIndex,
                    "resource query filters must be rejected before repository request construction"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Routed_Resource_Pipelines : PipelineOrderingTests
    {
        private static List<Type> GetRoutedResourcePipelineStepTypes(string factoryMethodName)
        {
            var services = new ServiceCollection();

            services.Configure<JwtAuthenticationOptions>(options => { });
            services.AddTransient<JwtAuthenticationMiddleware>();
            services.AddTransient<IJwtValidationService>(_ => A.Fake<IJwtValidationService>());
            services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
                NullLogger<JwtAuthenticationMiddleware>.Instance
            );

            services.AddTransient<ResolveDataStoreMiddleware>();
            services.AddSingleton<IApplicationContextProvider>(A.Fake<IApplicationContextProvider>());
            services.AddSingleton<IDataStoreProvider>(A.Fake<IDataStoreProvider>());
            services.AddSingleton<IDataStoreSelection>(A.Fake<IDataStoreSelection>());
            services.AddTransient<ILogger<ResolveDataStoreMiddleware>>(_ =>
                NullLogger<ResolveDataStoreMiddleware>.Instance
            );

            var appSettingsOptions = Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false }
            );
            services.AddSingleton(appSettingsOptions);
            services.AddSingleton<IDatabaseFingerprintReader, NullDatabaseFingerprintReader>();
            services.AddSingleton<DatabaseFingerprintProvider>();
            services.AddTransient<ValidateDatabaseFingerprintMiddleware>();
            services.AddTransient<ILogger<ValidateDatabaseFingerprintMiddleware>>(_ =>
                NullLogger<ValidateDatabaseFingerprintMiddleware>.Instance
            );

            TestHelper.AddResourceKeyValidationServices(services);
            TestHelper.AddMappingSetResolutionServices(services);

            services.AddSingleton<IProfileService>(A.Fake<IProfileService>());
            services.AddTransient<ProfileResolutionMiddleware>();
            services.AddTransient<ILogger<ProfileResolutionMiddleware>>(_ =>
                NullLogger<ProfileResolutionMiddleware>.Instance
            );

            services.AddSingleton<ICompiledSchemaCache>(A.Fake<ICompiledSchemaCache>());
            services.AddTransient<ProfileWritePipelineMiddleware>();
            services.AddTransient<ILogger<ProfileWritePipelineMiddleware>>(_ =>
                NullLogger<ProfileWritePipelineMiddleware>.Instance
            );

            var serviceProvider = services.BuildServiceProvider();

            var apiService = new ApiService(
                A.Fake<IApiSchemaProvider>(),
                A.Fake<IEffectiveApiSchemaProvider>(),
                A.Fake<IClaimSetProvider>(),
                A.Fake<IDocumentValidator>(),
                A.Fake<IMatchingDocumentUuidsValidator>(),
                A.Fake<IEqualityConstraintValidator>(),
                A.Fake<IDecimalValidator>(),
                NullLogger<ApiService>.Instance,
                NullLoggerFactory.Instance,
                appSettingsOptions,
                ResiliencePipeline.Empty,
                A.Fake<ResourceLoadOrderCalculator>(),
                serviceProvider,
                A.Fake<IServiceScopeFactory>(),
                A.Fake<CachedClaimSetProvider>(),
                A.Fake<IResourceDependencyGraphMLFactory>(),
                A.Fake<IProfileService>()
            );

            return GetStepTypes(apiService, factoryMethodName);
        }

        [TestCase("CreateUpsertPipeline")]
        [TestCase("CreateUpdatePipeline")]
        [TestCase("CreateDeleteByIdPipeline")]
        public void It_places_validate_route_semantics_after_validate_endpoint(string factoryMethodName)
        {
            var stepTypes = GetRoutedResourcePipelineStepTypes(factoryMethodName);
            var validateEndpointIndex = stepTypes.IndexOf(typeof(ValidateEndpointMiddleware));
            var validateRouteSemanticsIndex = stepTypes.IndexOf(typeof(ValidateRouteSemanticsMiddleware));

            validateEndpointIndex.Should().BeGreaterThanOrEqualTo(0);
            validateRouteSemanticsIndex.Should().BeGreaterThanOrEqualTo(0);
            validateRouteSemanticsIndex
                .Should()
                .BeGreaterThan(
                    validateEndpointIndex,
                    "ValidateRouteSemanticsMiddleware must run after ValidateEndpointMiddleware"
                );
        }

        [TestCase("CreateUpsertPipeline")]
        [TestCase("CreateUpdatePipeline")]
        public void It_places_validate_route_semantics_before_body_parsing_on_body_write_pipelines(
            string factoryMethodName
        )
        {
            var stepTypes = GetRoutedResourcePipelineStepTypes(factoryMethodName);
            var validateRouteSemanticsIndex = stepTypes.IndexOf(typeof(ValidateRouteSemanticsMiddleware));
            var parseBodyIndex = stepTypes.IndexOf(typeof(ParseBodyMiddleware));

            validateRouteSemanticsIndex.Should().BeGreaterThanOrEqualTo(0);
            parseBodyIndex.Should().BeGreaterThanOrEqualTo(0);
            validateRouteSemanticsIndex
                .Should()
                .BeLessThan(
                    parseBodyIndex,
                    "ValidateRouteSemanticsMiddleware must reject invalid write route semantics before request body parsing"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Get_Token_Info_Pipeline : PipelineOrderingTests
    {
        private List<Type> _stepTypes = [];

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();

            services.Configure<JwtAuthenticationOptions>(options => { });
            services.AddTransient<JwtAuthenticationMiddleware>();
            services.AddTransient<IJwtValidationService>(_ => A.Fake<IJwtValidationService>());
            services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
                NullLogger<JwtAuthenticationMiddleware>.Instance
            );

            services.AddTransient<ResolveDataStoreMiddleware>();
            services.AddSingleton<IApplicationContextProvider>(A.Fake<IApplicationContextProvider>());
            services.AddSingleton<IDataStoreProvider>(A.Fake<IDataStoreProvider>());
            services.AddSingleton<IDataStoreSelection>(A.Fake<IDataStoreSelection>());
            services.AddTransient<ILogger<ResolveDataStoreMiddleware>>(_ =>
                NullLogger<ResolveDataStoreMiddleware>.Instance
            );

            var appSettingsOptions = Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false }
            );
            services.AddSingleton(appSettingsOptions);
            services.AddSingleton<IDatabaseFingerprintReader, NullDatabaseFingerprintReader>();
            services.AddSingleton<DatabaseFingerprintProvider>();
            services.AddTransient<ValidateDatabaseFingerprintMiddleware>();
            services.AddTransient<ILogger<ValidateDatabaseFingerprintMiddleware>>(_ =>
                NullLogger<ValidateDatabaseFingerprintMiddleware>.Instance
            );

            TestHelper.AddResourceKeyValidationServices(services);

            var claimSetProvider = A.Fake<IClaimSetProvider>();
            var profileService = A.Fake<IProfileService>();
            services.AddSingleton(A.Fake<ITokenInfoRelationalMappingSetResolver>());

            services.AddSingleton(claimSetProvider);
            services.AddSingleton(profileService);
            services.AddTransient<GetTokenInfoHandler>();
            services.AddTransient<ILogger<GetTokenInfoHandler>>(_ =>
                NullLogger<GetTokenInfoHandler>.Instance
            );

            var serviceProvider = services.BuildServiceProvider();

            var apiService = new ApiService(
                A.Fake<IApiSchemaProvider>(),
                A.Fake<IEffectiveApiSchemaProvider>(),
                claimSetProvider,
                A.Fake<IDocumentValidator>(),
                A.Fake<IMatchingDocumentUuidsValidator>(),
                A.Fake<IEqualityConstraintValidator>(),
                A.Fake<IDecimalValidator>(),
                NullLogger<ApiService>.Instance,
                NullLoggerFactory.Instance,
                appSettingsOptions,
                ResiliencePipeline.Empty,
                A.Fake<ResourceLoadOrderCalculator>(),
                serviceProvider,
                A.Fake<IServiceScopeFactory>(),
                A.Fake<CachedClaimSetProvider>(),
                A.Fake<IResourceDependencyGraphMLFactory>(),
                profileService
            );

            _stepTypes = GetStepTypes(apiService, "CreateGetTokenInfoPipeline");
        }

        [Test]
        public void It_contains_ValidateDatabaseFingerprintMiddleware()
        {
            _stepTypes.Should().Contain(typeof(ValidateDatabaseFingerprintMiddleware));
        }

        [Test]
        public void It_omits_ParsePathMiddleware()
        {
            _stepTypes.Should().NotContain(typeof(ParsePathMiddleware));
        }

        [Test]
        public void It_defers_ResolveMappingSetMiddleware_until_a_relational_lookup_is_needed()
        {
            _stepTypes.Should().NotContain(typeof(ResolveMappingSetMiddleware));
        }

        [Test]
        public void It_places_fingerprint_validation_before_schema_dependent_middleware()
        {
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));
            var apiSchemaValidationIndex = _stepTypes.IndexOf(typeof(ApiSchemaValidationMiddleware));

            fingerprintIndex.Should().BeGreaterThanOrEqualTo(0);
            apiSchemaValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            fingerprintIndex
                .Should()
                .BeLessThan(
                    apiSchemaValidationIndex,
                    "ValidateDatabaseFingerprintMiddleware must run before schema-dependent middleware"
                );
        }

        [Test]
        public void It_places_fingerprint_validation_before_GetTokenInfoHandler()
        {
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));
            var handlerIndex = _stepTypes.IndexOf(typeof(GetTokenInfoHandler));

            fingerprintIndex.Should().BeGreaterThanOrEqualTo(0);
            handlerIndex.Should().BeGreaterThanOrEqualTo(0);
            fingerprintIndex
                .Should()
                .BeLessThan(
                    handlerIndex,
                    "ValidateDatabaseFingerprintMiddleware must run before GetTokenInfoHandler"
                );
        }

        [Test]
        public void It_contains_ValidateResourceKeySeedMiddleware()
        {
            _stepTypes.Should().Contain(typeof(ValidateResourceKeySeedMiddleware));
        }

        [Test]
        public void It_places_resource_key_validation_after_fingerprint_validation()
        {
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));

            fingerprintIndex.Should().BeGreaterThanOrEqualTo(0);
            resourceKeyIndex
                .Should()
                .BeGreaterThan(
                    fingerprintIndex,
                    "ValidateResourceKeySeedMiddleware must come after ValidateDatabaseFingerprintMiddleware"
                );
        }

        [Test]
        public void It_places_resource_key_validation_before_schema_dependent_middleware()
        {
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));
            var apiSchemaValidationIndex = _stepTypes.IndexOf(typeof(ApiSchemaValidationMiddleware));

            resourceKeyIndex.Should().BeGreaterThanOrEqualTo(0);
            apiSchemaValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            resourceKeyIndex
                .Should()
                .BeLessThan(
                    apiSchemaValidationIndex,
                    "ValidateResourceKeySeedMiddleware must run before schema-dependent middleware"
                );
        }

        [Test]
        public void It_places_resource_key_validation_before_GetTokenInfoHandler()
        {
            var resourceKeyIndex = _stepTypes.IndexOf(typeof(ValidateResourceKeySeedMiddleware));
            var handlerIndex = _stepTypes.IndexOf(typeof(GetTokenInfoHandler));

            resourceKeyIndex.Should().BeGreaterThanOrEqualTo(0);
            handlerIndex.Should().BeGreaterThanOrEqualTo(0);
            resourceKeyIndex
                .Should()
                .BeLessThan(
                    handlerIndex,
                    "ValidateResourceKeySeedMiddleware must run before token_info can resolve relational mapping metadata"
                );
        }
    }
}
