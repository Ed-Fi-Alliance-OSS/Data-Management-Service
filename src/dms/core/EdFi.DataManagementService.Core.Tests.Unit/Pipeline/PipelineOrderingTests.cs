// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
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
    [TestFixture]
    [Parallelizable]
    public class Given_Common_Initial_Steps : PipelineOrderingTests
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

            services.AddTransient<ResolveDmsInstanceMiddleware>();
            services.AddSingleton<IApplicationContextProvider>(A.Fake<IApplicationContextProvider>());
            services.AddSingleton<IDmsInstanceProvider>(A.Fake<IDmsInstanceProvider>());
            services.AddSingleton<IDmsInstanceSelection>(A.Fake<IDmsInstanceSelection>());
            services.AddTransient<ILogger<ResolveDmsInstanceMiddleware>>(_ =>
                NullLogger<ResolveDmsInstanceMiddleware>.Instance
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
                appSettingsOptions,
                A.Fake<IAuthorizationServiceFactory>(),
                ResiliencePipeline.Empty,
                A.Fake<ResourceLoadOrderCalculator>(),
                serviceProvider,
                A.Fake<IServiceScopeFactory>(),
                A.Fake<CachedClaimSetProvider>(),
                A.Fake<IResourceDependencyGraphMLFactory>(),
                A.Fake<IProfileService>()
            );

            var method = typeof(ApiService).GetMethod(
                "GetCommonInitialSteps",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            method.Should().NotBeNull("GetCommonInitialSteps should exist on ApiService");
            var steps = (List<IPipelineStep>)method!.Invoke(apiService, null)!;
            _stepTypes = steps.Select(s => s.GetType()).ToList();
        }

        [Test]
        public void It_contains_ValidateDatabaseFingerprintMiddleware()
        {
            _stepTypes.Should().Contain(typeof(ValidateDatabaseFingerprintMiddleware));
        }

        [Test]
        public void It_places_fingerprint_validation_after_resolve_dms_instance()
        {
            var resolveIndex = _stepTypes.IndexOf(typeof(ResolveDmsInstanceMiddleware));
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));

            resolveIndex.Should().BeGreaterThanOrEqualTo(0);
            fingerprintIndex
                .Should()
                .BeGreaterThan(
                    resolveIndex,
                    "ValidateDatabaseFingerprintMiddleware must come after ResolveDmsInstanceMiddleware"
                );
        }

        [Test]
        public void It_places_fingerprint_validation_as_last_common_step()
        {
            var fingerprintIndex = _stepTypes.IndexOf(typeof(ValidateDatabaseFingerprintMiddleware));
            fingerprintIndex
                .Should()
                .Be(
                    _stepTypes.Count - 1,
                    "ValidateDatabaseFingerprintMiddleware should be the last common initial step"
                );
        }

        [Test]
        public void It_does_not_contain_schema_dependent_middleware()
        {
            // Schema-dependent middleware (ApiSchemaValidationMiddleware, ProvideApiSchemaMiddleware)
            // must NOT be in the common initial steps — they are added per-pipeline after these steps.
            // This ensures ValidateDatabaseFingerprintMiddleware always runs before them.
            _stepTypes.Should().NotContain(typeof(ApiSchemaValidationMiddleware));
            _stepTypes.Should().NotContain(typeof(ProvideApiSchemaMiddleware));
        }
    }
}
