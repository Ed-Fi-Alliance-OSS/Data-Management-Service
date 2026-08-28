// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Tests.Unit.Middleware;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.CustomValidation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;
using Polly.CircuitBreaker;

namespace EdFi.DataManagementService.Core.Tests.Unit.Pipeline;

[TestFixture]
[Parallelizable]
public class PipelineOrderingTests
{
    /// <summary>
    /// The steps a pipeline factory actually built, as constructed instances. Most fixtures here
    /// only compare step types, but a step configured by a constructor argument can only be checked
    /// by exercising the instance the factory produced.
    /// </summary>
    private static List<IPipelineStep> GetSteps(ApiService apiService, string factoryMethodName)
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

        return (List<IPipelineStep>)field!.GetValue(pipeline)!;
    }

    private static List<Type> GetStepTypes(ApiService apiService, string factoryMethodName)
    {
        return GetSteps(apiService, factoryMethodName).Select(step => step.GetType()).ToList();
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

            TestHelper.AddCollectionPagingTelemetry(services);

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
                A.Fake<IProfileService>(),
                new CircuitBreakerSettings()
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

    /// <summary>
    /// The partitions pipeline is the GET-many pipeline with its paging validation and hydrating
    /// handler replaced. Asserting the whole composed sequence against the query pipeline's, rather
    /// than spot-checking a few steps, is what makes a boundary set provably calculated over the same
    /// authorized candidate relation a page of the same request would be selected from: a step silently
    /// added to or dropped from either one fails here.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Partitions_Pipeline : PipelineOrderingTests
    {
        private List<Type> _partitionStepTypes = [];
        private List<Type> _queryStepTypes = [];

        [SetUp]
        public void Setup()
        {
            _partitionStepTypes = GetRoutedResourcePipelineStepTypes("CreateGetPartitionsPipeline");
            _queryStepTypes = GetRoutedResourcePipelineStepTypes("CreateQueryPipeline");
        }

        /// <summary>
        /// The two substitutions that turn a query pipeline step into its partitions counterpart.
        /// </summary>
        private static Type SubstitutePartitionStep(Type queryStepType)
        {
            if (queryStepType == typeof(ValidateQueryMiddleware))
            {
                return typeof(ValidatePartitionQueryMiddleware);
            }

            if (queryStepType == typeof(QueryRequestHandler))
            {
                return typeof(PartitionRequestHandler);
            }

            return queryStepType;
        }

        [Test]
        public void It_differs_from_the_query_pipeline_only_in_its_validation_and_handler()
        {
            _partitionStepTypes.Should().Equal([.. _queryStepTypes.Select(SubstitutePartitionStep)]);
        }

        // The candidate relation is authorized before the boundary statement runs, exactly as it is
        // before a page is selected.
        [Test]
        public void It_places_authorization_between_validation_and_the_handler()
        {
            var validationIndex = _partitionStepTypes.IndexOf(typeof(ValidatePartitionQueryMiddleware));
            var authorizationIndex = _partitionStepTypes.IndexOf(
                typeof(ResourceActionAuthorizationMiddleware)
            );
            var filtersIndex = _partitionStepTypes.IndexOf(typeof(ProvideAuthorizationFiltersMiddleware));
            var handlerIndex = _partitionStepTypes.IndexOf(typeof(PartitionRequestHandler));

            validationIndex.Should().BeGreaterThanOrEqualTo(0);
            authorizationIndex.Should().BeGreaterThan(validationIndex);
            filtersIndex.Should().BeGreaterThan(authorizationIndex);
            handlerIndex.Should().BeGreaterThan(filtersIndex);
        }

        // This pipeline only ever serves GET, so the middleware that rejects a method for an operation
        // has nothing to do here. A write method never reaches it: dispatch routes writes to their own
        // pipelines, where that middleware answers with the partitions Allow set.
        [Test]
        public void It_omits_route_semantics_validation()
        {
            _partitionStepTypes.Should().NotContain(typeof(ValidateRouteSemanticsMiddleware));
        }

        [Test]
        public void It_never_validates_page_paging()
        {
            _partitionStepTypes.Should().NotContain(typeof(ValidateQueryMiddleware));
            _partitionStepTypes.Should().NotContain(typeof(QueryRequestHandler));
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

            TestHelper.AddCollectionPagingTelemetry(services);

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
                A.Fake<IProfileService>(),
                new CircuitBreakerSettings()
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

    /// <summary>
    /// Builds a routed-resource pipeline through the real ApiService factory, so the composed sequence
    /// under test is the one production builds.
    /// </summary>
    private static List<Type> GetRoutedResourcePipelineStepTypes(string factoryMethodName) =>
        GetStepTypes(BuildRoutedResourceApiService(), factoryMethodName);

    /// <summary>
    /// The real ApiService, with <paramref name="collectionPagingTelemetry" /> registered as the
    /// telemetry its pipeline factories resolve, and <paramref name="configureServices" /> given the
    /// chance to add registrations none of the other fixtures in this file need.
    /// </summary>
    private static ApiService BuildRoutedResourceApiService(
        ICollectionPagingTelemetry? collectionPagingTelemetry = null,
        Action<IServiceCollection>? configureServices = null
    )
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

        services.AddSingleton(collectionPagingTelemetry ?? NoOpCollectionPagingTelemetry.Instance);

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

        configureServices?.Invoke(services);

        var serviceProvider = services.BuildServiceProvider();

        return new ApiService(
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
            A.Fake<IProfileService>(),
            new CircuitBreakerSettings()
        );
    }

    /// <summary>
    /// The query-validation step is the only instrumented type with two construction sites, and the
    /// Change Query one must stay uncounted: those endpoints do not page by cursor at all, so a
    /// /deletes?limit=abc fault is not a collection read.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Shared_Query_Validation_Step : PipelineOrderingTests
    {
        /// <summary>
        /// Whether the query-validation step a pipeline factory composed counts its rejections as
        /// collection-paging traffic.
        /// </summary>
        /// <remarks>
        /// Exercises the instance the real factory produced, which is the only way to see a difference
        /// that lives entirely in a constructor argument. The step is executed directly rather than
        /// through the whole pipeline so the assertion depends on nothing before it.
        /// </remarks>
        private static async Task<IReadOnlyList<CollectionPagingMeasurement>> RecordRejectionFrom(
            string factoryMethodName
        )
        {
            RecordingCollectionPagingTelemetry telemetry = new();
            List<IPipelineStep> steps = GetSteps(BuildRoutedResourceApiService(telemetry), factoryMethodName);
            IPipelineStep validation = steps.OfType<ValidateQueryMiddleware>().Single();

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["limit"] = "-1" },
                TraceId: new TraceId("pipeline-composition"),
                RouteQualifiers: []
            );
            RequestInfo requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider);

            await validation.Execute(requestInfo, TestHelper.NullNext);

            // Proves the step really answered the request. Without this an empty recorder could mean
            // the fault never reached a rejecting exit at all.
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            return telemetry.Measurements;
        }

        [Test]
        public async Task It_counts_a_get_many_rejection()
        {
            IReadOnlyList<CollectionPagingMeasurement> measurements = await RecordRejectionFrom(
                "CreateQueryPipeline"
            );

            measurements.Should().ContainSingle().Which.Outcome.Should().Be("validation_rejected");
        }

        // The guard against a wiring change at the tracked-changes construction site silently folding
        // Change Query faults into the collection-paging rejection rate, which would move every
        // dashboard built on it with no other symptom.
        [Test]
        public async Task It_counts_nothing_for_a_change_query_rejection()
        {
            IReadOnlyList<CollectionPagingMeasurement> measurements = await RecordRejectionFrom(
                "CreateGetTrackedChangesPipeline"
            );

            measurements.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Routed_Resource_Pipelines : PipelineOrderingTests
    {
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

        // "Immediately after authorization filters" and "immediately before the terminal handler"
        // both have to hold for the slot to be pinned exactly: a step merely somewhere after
        // ProvideAuthorizationFiltersMiddleware and somewhere before the handler could still drift
        // to a different position without either relative-ordering test noticing.
        [TestCase("CreateUpsertPipeline")]
        [TestCase("CreateUpdatePipeline")]
        public void It_places_custom_resource_validation_immediately_after_authorization_filters_and_immediately_before_the_terminal_handler(
            string factoryMethodName
        )
        {
            var stepTypes = GetRoutedResourcePipelineStepTypes(factoryMethodName);
            var authorizationFiltersIndex = stepTypes.IndexOf(typeof(ProvideAuthorizationFiltersMiddleware));
            var customValidationIndex = stepTypes.IndexOf(typeof(CustomResourceValidationMiddleware));

            authorizationFiltersIndex.Should().BeGreaterThanOrEqualTo(0);
            customValidationIndex.Should().BeGreaterThanOrEqualTo(0);
            customValidationIndex
                .Should()
                .Be(
                    authorizationFiltersIndex + 1,
                    "CustomResourceValidationMiddleware must run immediately after ProvideAuthorizationFiltersMiddleware"
                );
            customValidationIndex
                .Should()
                .Be(
                    stepTypes.Count - 2,
                    "CustomResourceValidationMiddleware must run immediately before the terminal handler"
                );
        }

        /// <summary>
        /// GetTokenInfoHandler and AvailableChangeVersionsHandler are both resolved through
        /// GetRequiredService, and BuildRoutedResourceApiService does not carry either
        /// registration - no other fixture in this file needs them. This builder adds only what
        /// those two factories require to build without throwing, so an omitted registration never
        /// masquerades as an absent CustomResourceValidationMiddleware.
        /// </summary>
        private static ApiService BuildOmissionCheckApiService() =>
            BuildRoutedResourceApiService(configureServices: services =>
            {
                services.AddSingleton<IClaimSetProvider>(A.Fake<IClaimSetProvider>());
                services.AddSingleton(A.Fake<ITokenInfoRelationalMappingSetResolver>());
                services.AddTransient<GetTokenInfoHandler>();
                services.AddTransient<ILogger<GetTokenInfoHandler>>(_ =>
                    NullLogger<GetTokenInfoHandler>.Instance
                );

                services.AddTransient<AvailableChangeVersionsHandler>();
                services.AddTransient<ILogger<AvailableChangeVersionsHandler>>(_ =>
                    NullLogger<AvailableChangeVersionsHandler>.Instance
                );
            });

        // Custom validation is POST and PUT only. DELETE belongs in this list even though the
        // design treats it as a stated non-goal: without a case for it here, an implementation that
        // added the step to CreateDeleteByIdPipeline would satisfy every other criterion in this
        // epic and still pass.
        [TestCase("CreateGetByIdPipeline")]
        [TestCase("CreateQueryPipeline")]
        [TestCase("CreateDeleteByIdPipeline")]
        [TestCase("CreateGetPartitionsPipeline")]
        [TestCase("CreateGetTokenInfoPipeline")]
        [TestCase("CreateGetAvailableChangeVersionsPipeline")]
        [TestCase("CreateGetTrackedChangesPipeline")]
        [TestCase("CreateMethodNotAllowedPipeline")]
        [TestCase("CreateTrackedChangeMethodNotAllowedPipeline")]
        public void It_is_absent_from_every_non_write_pipeline(string factoryMethodName)
        {
            var stepTypes = GetStepTypes(BuildOmissionCheckApiService(), factoryMethodName);

            stepTypes.Should().NotContain(typeof(CustomResourceValidationMiddleware));
        }

        // The enum each write pipeline wires CustomResourceValidationMiddleware with is a
        // constructor argument fixed at the wiring site in ApiService.cs, so a test that news up the
        // middleware directly (as CustomResourceValidationMiddlewareTests does) cannot observe it. A
        // swapped or duplicated mapping between the two pipelines otherwise leaves every other test
        // in this plan green.
        [TestCase("CreateUpsertPipeline", CustomValidationOperation.Upsert)]
        [TestCase("CreateUpdatePipeline", CustomValidationOperation.Update)]
        public async Task It_wires_the_step_with_the_operation_matching_its_own_pipeline(
            string factoryMethodName,
            CustomValidationOperation expectedOperation
        )
        {
            var validator = new CustomResourceValidationMiddlewareTests.FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };
            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            IPipelineStep step = GetSteps(BuildRoutedResourceApiService(), factoryMethodName)
                .OfType<CustomResourceValidationMiddleware>()
                .Single();

            RequestInfo requestInfo = CustomResourceValidationMiddlewareTests.BuildRequestInfo(
                scopedServiceProvider
            );

            await step.Execute(requestInfo, TestHelper.NullNext);

            validator.ReceivedOperations.Should().ContainSingle().Which.Should().Be(expectedOperation);
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
                profileService,
                new CircuitBreakerSettings()
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

    /// <summary>
    /// The two pipelines that answer an unsupported HTTP method. They are near-identical by design and
    /// differ in exactly one step - the path parser - which no other test can see: the terminal's own
    /// unit tests build PathComponents by hand, and the frontend routing tests fake IApiService, so
    /// neither ever runs a parse step.
    ///
    /// The registrations below are deliberately smaller than every fixture above. Neither pipeline
    /// reaches a backend, so the fingerprint, resource-key-seed and mapping-set services are absent and
    /// resolution would fail outright if a future change put those steps back.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Method_Not_Allowed_Pipelines : PipelineOrderingTests
    {
        private const string DataRoutePipeline = "CreateMethodNotAllowedPipeline";
        private const string TrackedChangePipeline = "CreateTrackedChangeMethodNotAllowedPipeline";

        internal static ApiService BuildApiServiceWith(CircuitBreakerSettings circuitBreakerSettings) =>
            BuildApiService(circuitBreakerSettings);

        private static ApiService BuildApiService() => BuildApiService(new CircuitBreakerSettings());

        private static ApiService BuildApiService(CircuitBreakerSettings circuitBreakerSettings)
        {
            var services = new ServiceCollection();

            services.AddTransient<JwtAuthenticationMiddleware>();
            services.AddTransient<IJwtValidationService>(_ => A.Fake<IJwtValidationService>());
            services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
                NullLogger<JwtAuthenticationMiddleware>.Instance
            );

            services.AddTransient<ResolveDataStoreMiddleware>();
            services.AddSingleton<IDataStoreProvider>(A.Fake<IDataStoreProvider>());
            services.AddTransient<ILogger<ResolveDataStoreMiddleware>>(_ =>
                NullLogger<ResolveDataStoreMiddleware>.Instance
            );

            var appSettingsOptions = Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false }
            );
            services.AddSingleton(appSettingsOptions);

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
                A.Fake<IProfileService>(),
                circuitBreakerSettings
            );

            return apiService;
        }

        private static List<Type> GetMethodNotAllowedPipelineStepTypes(string factoryMethodName)
        {
            return GetStepTypes(BuildApiService(), factoryMethodName);
        }

        [Test]
        public void It_parses_the_data_path_in_the_data_route_pipeline()
        {
            // ParseTrackedChangePathMiddleware here instead would miss /ed-fi/schools, because its
            // regex requires a third segment, turning every collection-route 405 into a 404.
            GetMethodNotAllowedPipelineStepTypes(DataRoutePipeline)
                .Should()
                .Contain(typeof(ParsePathMiddleware));
        }

        [Test]
        public void It_parses_the_operation_suffix_in_the_tracked_change_pipeline()
        {
            // ParsePathMiddleware here instead would read "deletes" as a document id and reject it as
            // a malformed uuid, turning every tracked-change 405 into a 400.
            GetMethodNotAllowedPipelineStepTypes(TrackedChangePipeline)
                .Should()
                .Contain(typeof(ParseTrackedChangePathMiddleware));
        }

        [TestCase(DataRoutePipeline)]
        [TestCase(TrackedChangePipeline)]
        public void It_answers_405_only_after_the_endpoint_is_known_to_exist(string factoryMethodName)
        {
            var stepTypes = GetMethodNotAllowedPipelineStepTypes(factoryMethodName);
            var validateEndpointIndex = stepTypes.IndexOf(typeof(ValidateEndpointMiddleware));
            var terminalIndex = stepTypes.IndexOf(typeof(MethodNotAllowedMiddleware));

            validateEndpointIndex.Should().BeGreaterThanOrEqualTo(0);
            terminalIndex
                .Should()
                .BeGreaterThan(
                    validateEndpointIndex,
                    "an unknown project namespace or resource must answer 404 rather than 405, matching ODS/API's existence-then-method ordering"
                );
        }

        [TestCase(DataRoutePipeline)]
        [TestCase(TrackedChangePipeline)]
        public void It_contains_ResolveDataStoreMiddleware(string factoryMethodName)
        {
            GetMethodNotAllowedPipelineStepTypes(factoryMethodName)
                .Should()
                .Contain(
                    typeof(ResolveDataStoreMiddleware),
                    "an unsupported method must not answer 405 where the equivalent supported request answers 403 or 404 for want of an authorized or matching instance"
                );
        }

        /// <summary>
        /// The regression guard for the route-family discriminator. MethodNotAllowedMiddleware takes
        /// which family it terminates as a constructor argument, so no assertion over step types can
        /// see it wired, and the middleware's own tests pass the flag in themselves. This runs the
        /// terminal each factory actually built and reads the Allow header back off it, which is the
        /// only place a pipeline constructed with the wrong flag shows up.
        /// </summary>
        [TestCase(DataRoutePipeline, "GET, POST")]
        [TestCase(TrackedChangePipeline, "GET")]
        public async Task It_builds_the_terminal_for_its_own_route_family(
            string factoryMethodName,
            string expectedAllow
        )
        {
            IPipelineStep terminal = GetSteps(BuildApiService(), factoryMethodName)
                .Single(step => step is MethodNotAllowedMiddleware);

            // Both parse steps classify their path as the Collection operation, which is why the
            // terminal cannot read the route family off the request and has to be told at construction.
            RequestInfo requestInfo = No.RequestInfo("method-not-allowed-wiring-trace-id");
            requestInfo.Method = RequestMethod.UNSUPPORTED;
            requestInfo.UnsupportedMethodName = "PATCH";
            requestInfo.PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new("schools"),
                Operation: ResourcePathOperation.Collection.Instance
            );

            await terminal.Execute(requestInfo, TestHelper.NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(405);
            requestInfo.FrontendResponse.Headers.Should().Contain("Allow", expectedAllow);
        }
    }

    /// <summary>
    /// The break duration reaches the exception middleware from configuration, and every one of the
    /// circuit-breaker settings is a bare number of seconds, so wiring the wrong one would compile,
    /// pass every other test, and quote a plausible-looking Retry-After that has nothing to do with
    /// how long the circuit stays open. The values below are deliberately distinct so only the
    /// intended setting can produce the expected header.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Pipeline_Is_Built_With_Circuit_Breaker_Settings : PipelineOrderingTests
    {
        [Test]
        public async Task It_quotes_the_break_duration_as_retry_after_not_another_setting()
        {
            var apiService = Given_The_Method_Not_Allowed_Pipelines.BuildApiServiceWith(
                new CircuitBreakerSettings
                {
                    FailureRatio = 0.1,
                    SamplingDurationSeconds = 120,
                    MinimumThroughput = 20,
                    BreakDurationSeconds = 7,
                }
            );

            var exceptionMiddleware = GetSteps(apiService, "CreateMethodNotAllowedPipeline")
                .Single(step => step is CoreExceptionLoggingMiddleware);

            RequestInfo requestInfo = No.RequestInfo("circuit-break-duration-wiring-trace-id");
            await exceptionMiddleware.Execute(
                requestInfo,
                () => throw new BrokenCircuitException("circuit is open")
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(503);
            requestInfo.FrontendResponse.Headers.Should().Contain("Retry-After", "7");
        }
    }
}
