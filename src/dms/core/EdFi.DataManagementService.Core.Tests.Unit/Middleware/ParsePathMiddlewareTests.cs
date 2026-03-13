// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Json;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
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
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ParsePathMiddlewareTests
{
    private static readonly Dictionary<string, string> AuthorizationHeaders = new()
    {
        ["Authorization"] = "Bearer test-token",
    };

    internal static IPipelineStep Middleware()
    {
        return new ParsePathMiddleware(NullLogger.Instance);
    }

    private static FrontendRequest CreateFrontendRequest(string path)
    {
        return new FrontendRequest(
            Body: null,
            Form: null,
            Headers: AuthorizationHeaders,
            Path: path,
            QueryParameters: [],
            TraceId: new TraceId("test-trace-id"),
            RouteQualifiers: []
        );
    }

    private static ApiService CreateApiService(IDatabaseFingerprintReader fingerprintReader)
    {
        var services = new ServiceCollection();

        services.Configure<JwtAuthenticationOptions>(options => { });

        var jwtValidationService = A.Fake<IJwtValidationService>();
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                    (
                        new ClaimsPrincipal(),
                        new ClientAuthorizations(
                            ClientId: "test-client",
                            TokenId: "test-token",
                            ClaimSetName: "test-claimset",
                            EducationOrganizationIds: [],
                            NamespacePrefixes: [],
                            DmsInstanceIds: [new DmsInstanceId(1)]
                        )
                    )
                )
            );

        services.AddSingleton<IJwtValidationService>(jwtValidationService);
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
            NullLogger<JwtAuthenticationMiddleware>.Instance
        );

        services.AddTransient<ResolveDmsInstanceMiddleware>();

        var dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
        A.CallTo(() => dmsInstanceProvider.RefreshInstancesIfExpiredAsync(A<string?>._))
            .Returns(Task.CompletedTask);
        A.CallTo(() => dmsInstanceProvider.GetById(1, A<string?>.Ignored))
            .Returns(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "Test",
                    InstanceName: "Test Instance",
                    ConnectionString: "Server=test;Database=testdb",
                    RouteContext: []
                )
            );

        services.AddSingleton<IDmsInstanceProvider>(dmsInstanceProvider);
        services.AddScoped<IDmsInstanceSelection>(_ => A.Fake<IDmsInstanceSelection>());
        services.AddTransient<ILogger<ResolveDmsInstanceMiddleware>>(_ =>
            NullLogger<ResolveDmsInstanceMiddleware>.Instance
        );

        var appSettingsOptions = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                MaskRequestBodyInLogs = false,
                UseRelationalBackend = true,
            }
        );

        services.AddSingleton(appSettingsOptions);
        services.AddSingleton(fingerprintReader);
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
            appSettingsOptions,
            A.Fake<IAuthorizationServiceFactory>(),
            ResiliencePipeline.Empty,
            A.Fake<ResourceLoadOrderCalculator>(),
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            A.Fake<CachedClaimSetProvider>(),
            A.Fake<IResourceDependencyGraphMLFactory>(),
            A.Fake<IProfileService>()
        );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Empty_Path : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: "",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(404);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Path : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: "badpath",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(404);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Path_Without_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: "/ed-fi/endpointName",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_provides_correct_path_components()
        {
            _requestInfo?.PathComponents.Should().NotBe(No.PathComponents);

            _requestInfo?.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            _requestInfo?.PathComponents.EndpointName.Value.Should().Be("endpointName");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Path_With_Valid_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly string documentUuid = "7825fba8-0b3d-4fc9-ae72-5ad8194d3ce2";

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: $"/ed-fi/endpointName/{documentUuid}",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.PUT, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_provides_correct_path_components()
        {
            _requestInfo?.PathComponents.Should().NotBe(No.PathComponents);

            _requestInfo?.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            _requestInfo?.PathComponents.EndpointName.Value.Should().Be("endpointName");
            _requestInfo?.PathComponents.DocumentUuid.Value.Should().Be(documentUuid);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Path_With_Invalid_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: "/ed-fi/endpointName/invalidId",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_invalid_Id_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain("\"validationErrors\":{\"$.id\":[\"The value 'invalidId' is not valid.\"]}");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_With_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: $"/ed-fi/endpointName/{Guid.NewGuid()}",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response.Should().Contain("Method Not Allowed");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Put_With_Missing_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: "/ed-fi/endpointName/",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.PUT, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response.Should().Contain("Method Not Allowed");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Delete_With_Missing_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Form: null,
                Headers: [],
                Path: "/ed-fi/endpointName/",
                QueryParameters: [],
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.DELETE, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response.Should().Contain("Method Not Allowed");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Validation_Is_Enabled_And_The_Request_Path_Is_Invalid
        : ParsePathMiddlewareTests
    {
        private IFrontendResponse _response = null!;
        private IDatabaseFingerprintReader _fingerprintReader = null!;

        [SetUp]
        public async Task Setup()
        {
            _fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            var apiService = CreateApiService(_fingerprintReader);

            _response = await apiService.Get(CreateFrontendRequest("badpath"));
        }

        [Test]
        public void It_returns_status_404()
        {
            _response.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_does_not_read_the_database_fingerprint()
        {
            A.CallTo(() => _fingerprintReader.ReadFingerprintAsync(A<string>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Validation_Is_Enabled_And_The_Request_Resource_Id_Is_Invalid
        : ParsePathMiddlewareTests
    {
        private IFrontendResponse _response = null!;
        private IDatabaseFingerprintReader _fingerprintReader = null!;

        [SetUp]
        public async Task Setup()
        {
            _fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            var apiService = CreateApiService(_fingerprintReader);

            _response = await apiService.Get(CreateFrontendRequest("/ed-fi/students/invalidId"));
        }

        [Test]
        public void It_returns_status_400()
        {
            _response.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_read_the_database_fingerprint()
        {
            A.CallTo(() => _fingerprintReader.ReadFingerprintAsync(A<string>._)).MustNotHaveHappened();
        }
    }
}
