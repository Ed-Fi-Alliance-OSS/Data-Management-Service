// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class Given_Scope_Validation_Is_Enabled_For_Profile_Resolution_Middleware_Pipeline_Construction
{
    private Exception? _exception;
    private object? _pipeline;

    private static IOptions<AppSettings> CreateAppSettings() =>
        Options.Create(new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false });

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.Configure<JwtAuthenticationOptions>(options => { });
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddTransient<IJwtValidationService>(_ => A.Fake<IJwtValidationService>());
        services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
            NullLogger<JwtAuthenticationMiddleware>.Instance
        );

        services.AddTransient<ResolveDmsInstanceMiddleware>();
        services.AddScoped<IApplicationContextProvider>(_ => A.Fake<IApplicationContextProvider>());
        services.AddSingleton<IDmsInstanceProvider>(A.Fake<IDmsInstanceProvider>());
        services.AddSingleton<IDmsInstanceSelection>(A.Fake<IDmsInstanceSelection>());
        services.AddTransient<ILogger<ResolveDmsInstanceMiddleware>>(_ =>
            NullLogger<ResolveDmsInstanceMiddleware>.Instance
        );

        services.AddSingleton(CreateAppSettings());
        services.AddSingleton<IDatabaseFingerprintReader, NullDatabaseFingerprintReader>();
        services.AddSingleton<DatabaseFingerprintProvider>();
        services.AddTransient<ValidateDatabaseFingerprintMiddleware>();
        services.AddTransient<ILogger<ValidateDatabaseFingerprintMiddleware>>(_ =>
            NullLogger<ValidateDatabaseFingerprintMiddleware>.Instance
        );

        services.AddSingleton<IResourceKeyRowReader, NullResourceKeyRowReader>();
        services.AddSingleton<IResourceKeyValidator>(A.Fake<IResourceKeyValidator>());
        services.AddSingleton<ResourceKeyValidationCacheProvider>();
        services.AddSingleton<IEffectiveSchemaSetProvider>(A.Fake<IEffectiveSchemaSetProvider>());
        services.AddTransient<ValidateResourceKeySeedMiddleware>();
        services.AddTransient<ILogger<ValidateResourceKeySeedMiddleware>>(_ =>
            NullLogger<ValidateResourceKeySeedMiddleware>.Instance
        );

        services.AddSingleton<IProfileService>(A.Fake<IProfileService>());
        services.AddTransient<ProfileResolutionMiddleware>();
        services.AddTransient<ILogger<ProfileResolutionMiddleware>>(_ =>
            NullLogger<ProfileResolutionMiddleware>.Instance
        );

        services.AddTransient<ProfileFilteringMiddleware>();
        services.AddSingleton<IProfileResponseFilter>(A.Fake<IProfileResponseFilter>());
        services.AddTransient<ILogger<ProfileFilteringMiddleware>>(_ =>
            NullLogger<ProfileFilteringMiddleware>.Instance
        );

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ApiService CreateApiService(
        IServiceProvider serviceProvider,
        IOptions<AppSettings> appSettings
    )
    {
        return new ApiService(
            A.Fake<IApiSchemaProvider>(),
            A.Fake<IEffectiveApiSchemaProvider>(),
            A.Fake<IClaimSetProvider>(),
            A.Fake<IDocumentValidator>(),
            A.Fake<IMatchingDocumentUuidsValidator>(),
            A.Fake<IEqualityConstraintValidator>(),
            A.Fake<IDecimalValidator>(),
            NullLogger<ApiService>.Instance,
            appSettings,
            A.Fake<IAuthorizationServiceFactory>(),
            ResiliencePipeline.Empty,
            A.Fake<ResourceLoadOrderCalculator>(),
            serviceProvider,
            A.Fake<IServiceScopeFactory>(),
            A.Fake<CachedClaimSetProvider>(),
            A.Fake<IResourceDependencyGraphMLFactory>(),
            A.Fake<IProfileService>()
        );
    }

    [SetUp]
    public void Setup()
    {
        var serviceProvider = CreateServiceProvider();
        var appSettings = CreateAppSettings();
        var apiService = CreateApiService(serviceProvider, appSettings);
        var createGetByIdPipeline = typeof(ApiService).GetMethod(
            "CreateGetByIdPipeline",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        try
        {
            _pipeline = createGetByIdPipeline!.Invoke(apiService, null);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_builds_the_pipeline_without_resolving_scoped_services_from_the_root_provider()
    {
        _exception.Should().BeNull();
        _pipeline.Should().NotBeNull();
    }
}
