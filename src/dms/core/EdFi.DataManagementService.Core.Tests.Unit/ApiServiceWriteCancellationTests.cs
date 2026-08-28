// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit;

/// <summary>
/// Pins that ApiService.Upsert and ApiService.UpdateById carry a caller-supplied cancellation token
/// onto the RequestInfo they construct. The assignment is observed indirectly, through
/// JwtAuthenticationMiddleware, which hands requestInfo.RequestCancellationToken to
/// IJwtValidationService and runs in both write pipelines via GetCommonInitialSteps.
/// </summary>
[TestFixture]
[Parallelizable]
public class ApiServiceWriteCancellationTests
{
    private const string BearerToken = "valid-token";

    /// <summary>
    /// A real ApiService, built over a ServiceCollection carrying every service its write pipelines
    /// resolve while building (not just running) the pipeline. The caller's IJwtValidationService is
    /// registered as a singleton so the pipeline hands the request to the exact fake instance the
    /// test holds a reference to, rather than a fresh fake per resolution.
    /// </summary>
    private static ApiService BuildApiService(IJwtValidationService jwtValidationService)
    {
        var services = new ServiceCollection();

        services.Configure<JwtAuthenticationOptions>(options => { });
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddSingleton(jwtValidationService);
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

        services.AddSingleton<ICollectionPagingTelemetry>(NoOpCollectionPagingTelemetry.Instance);

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
    /// A well-formed Bearer request, so JwtAuthenticationMiddleware reaches IJwtValidationService
    /// instead of short-circuiting to a 401 for a missing or malformed header.
    /// </summary>
    private static FrontendRequest BuildFrontendRequest() =>
        new(
            Path: "/ed-fi/students",
            Body: "{}",
            Form: null,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {BearerToken}",
            },
            QueryParameters: [],
            TraceId: new TraceId("api-service-write-cancellation"),
            RouteQualifiers: []
        );

    /// <summary>
    /// A fake IJwtValidationService that records the cancellation token it was actually called
    /// with. Returns nulls so JwtAuthenticationMiddleware halts the pipeline right there with a 401,
    /// which is fine: the assertion is about the token the middleware forwarded, not the response.
    /// </summary>
    private static IJwtValidationService CreateJwtValidationServiceCapturing(
        Action<CancellationToken> captureToken
    )
    {
        var jwtValidationService = A.Fake<IJwtValidationService>();
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    BearerToken,
                    A<CancellationToken>._
                )
            )
            .Invokes((string _, CancellationToken token) => captureToken(token))
            .Returns(Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>((null, null)));
        return jwtValidationService;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Upsert_Is_Called : ApiServiceWriteCancellationTests
    {
        [Test]
        public async Task It_assigns_the_supplied_token_onto_the_RequestInfo_it_constructs()
        {
            using var cancellationSource = new CancellationTokenSource();
            CancellationToken capturedToken = default;
            var jwtValidationService = CreateJwtValidationServiceCapturing(token => capturedToken = token);
            var apiService = BuildApiService(jwtValidationService);

            await apiService.Upsert(BuildFrontendRequest(), cancellationSource.Token);

            capturedToken.Should().Be(cancellationSource.Token);
        }

        [Test]
        public async Task It_is_callable_with_no_token_supplied_leaving_the_RequestInfo_at_default()
        {
            // Seeded with a non-default token so an implementation that never assigned the
            // parameter could not pass this assertion by leaving the capture untouched.
            using var seedSource = new CancellationTokenSource();
            CancellationToken capturedToken = seedSource.Token;
            var jwtValidationService = CreateJwtValidationServiceCapturing(token => capturedToken = token);
            var apiService = BuildApiService(jwtValidationService);

            await apiService.Upsert(BuildFrontendRequest());

            capturedToken.Should().Be(CancellationToken.None);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_UpdateById_Is_Called : ApiServiceWriteCancellationTests
    {
        [Test]
        public async Task It_assigns_the_supplied_token_onto_the_RequestInfo_it_constructs()
        {
            using var cancellationSource = new CancellationTokenSource();
            CancellationToken capturedToken = default;
            var jwtValidationService = CreateJwtValidationServiceCapturing(token => capturedToken = token);
            var apiService = BuildApiService(jwtValidationService);

            await apiService.UpdateById(BuildFrontendRequest(), cancellationSource.Token);

            capturedToken.Should().Be(cancellationSource.Token);
        }

        [Test]
        public async Task It_is_callable_with_no_token_supplied_leaving_the_RequestInfo_at_default()
        {
            // Seeded with a non-default token so an implementation that never assigned the
            // parameter could not pass this assertion by leaving the capture untouched.
            using var seedSource = new CancellationTokenSource();
            CancellationToken capturedToken = seedSource.Token;
            var jwtValidationService = CreateJwtValidationServiceCapturing(token => capturedToken = token);
            var apiService = BuildApiService(jwtValidationService);

            await apiService.UpdateById(BuildFrontendRequest());

            capturedToken.Should().Be(CancellationToken.None);
        }
    }
}
