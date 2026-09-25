// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Proves the cross-middleware ordering invariants of the two identity pipelines (design.md D9, A13)
/// with a real ApiService built over a ServiceCollection, in the shape
/// ApiServiceWriteCancellationTests.BuildApiService uses: tenant existence is checked before claims,
/// client-to-tenant binding is checked before claims, and the capability gate runs before content-type
/// and body validation.
/// </summary>
public class IdentityPipelineOrderingTests
{
    private const string BearerToken = "valid-identity-pipeline-ordering-token";
    private const string ClaimSetName = "IdentityPipelineOrderingClaimSet";
    private const string ClientId = "identity-pipeline-ordering-client-id";

    private static ApiService BuildApiService(
        bool multiTenancy,
        IApplicationContextProvider applicationContextProvider,
        IClaimSetProvider claimSetProvider,
        IIdentityService identityService,
        IDataStoreProvider dataStoreProvider
    )
    {
        var services = new ServiceCollection();

        var jwtValidationService = A.Fake<IJwtValidationService>();
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    BearerToken,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                    (new ClaimsPrincipal(new ClaimsIdentity()), BuildClientAuthorizations())
                )
            );

        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddSingleton(jwtValidationService);
        services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
            NullLogger<JwtAuthenticationMiddleware>.Instance
        );

        services.AddScoped(_ => applicationContextProvider);
        services.AddScoped(_ => identityService);

        var appSettingsOptions = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                MaskRequestBodyInLogs = false,
                MultiTenancy = multiTenancy,
            }
        );
        services.AddSingleton(appSettingsOptions);

        var serviceProvider = services.BuildServiceProvider();

        var identityTenantSnapshot = new IdentityTenantSnapshot(
            dataStoreProvider,
            TimeProvider.System,
            A.Fake<IHostApplicationLifetime>(),
            NullLogger<IdentityTenantSnapshot>.Instance
        );

        return new ApiService(
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
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            A.Fake<CachedClaimSetProvider>(),
            A.Fake<IResourceDependencyGraphMLFactory>(),
            A.Fake<IProfileService>(),
            new CircuitBreakerSettings(),
            identityTenantSnapshot
        );
    }

    private static ClientAuthorizations BuildClientAuthorizations() =>
        new(
            TokenId: "identity-pipeline-ordering-token-id",
            ClientId: ClientId,
            ClaimSetName: ClaimSetName,
            EducationOrganizationIds: [],
            NamespacePrefixes: [],
            DataStoreIds: []
        );

    private static FrontendRequest BuildFrontendRequest(
        string? tenant = null,
        string? bodyParseErrorMessage = null
    ) =>
        new(
            Path: "/identity/v2/identities",
            Body: "{}",
            Form: null,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {BearerToken}",
            },
            QueryParameters: [],
            TraceId: new TraceId("identity-pipeline-ordering"),
            RouteQualifiers: [],
            Tenant: tenant,
            BodyParseErrorMessage: bodyParseErrorMessage
        );

    /// <summary>
    /// A2/A13: tenant existence is checked before claims are consulted, so a failing claim-set provider
    /// is never reached when the tenant itself does not exist.
    /// </summary>
    [TestFixture]
    public class Given_Tenant_Absence_When_Claims_Would_Fail : IdentityPipelineOrderingTests
    {
        private IFrontendResponse _response = null!;
        private IClaimSetProvider _claimSetProvider = null!;
        private IApplicationContextProvider _applicationContextProvider = null!;

        private static IDataStoreProvider EmptyTenantDataStoreProvider()
        {
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>([]));
            return dataStoreProvider;
        }

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .Throws<InvalidOperationException>();

            _applicationContextProvider = A.Fake<IApplicationContextProvider>();

            var apiService = BuildApiService(
                multiTenancy: true,
                _applicationContextProvider,
                _claimSetProvider,
                new NoIdentityService(),
                EmptyTenantDataStoreProvider()
            );

            _response = await apiService.IdentityCreate(
                BuildFrontendRequest(tenant: "north"),
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_404()
        {
            _response.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_never_consults_the_claim_set_provider()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void It_never_looks_up_the_application_context()
        {
            A.CallTo(() =>
                    _applicationContextProvider.GetApplicationByClientIdAsync(
                        ClientId,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    /// <summary>
    /// A13: client-to-tenant binding is checked before claims are consulted, so a NotFound binding
    /// never reaches a failing claim-set provider.
    /// </summary>
    [TestFixture]
    public class Given_A_NotFound_Client_Tenant_Binding_When_Claims_Would_Fail : IdentityPipelineOrderingTests
    {
        private IFrontendResponse _response = null!;
        private IClaimSetProvider _claimSetProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .Throws<InvalidOperationException>();

            var applicationContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() =>
                    applicationContextProvider.GetApplicationByClientIdAsync(
                        ClientId,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(Task.FromResult<ApplicationContextResult>(new ApplicationContextResult.NotFound()));

            var apiService = BuildApiService(
                multiTenancy: false,
                applicationContextProvider,
                _claimSetProvider,
                new NoIdentityService(),
                A.Fake<IDataStoreProvider>()
            );

            _response = await apiService.IdentityCreate(BuildFrontendRequest(), CancellationToken.None);
        }

        [Test]
        public void It_returns_401()
        {
            _response.StatusCode.Should().Be(401);
        }

        [Test]
        public void It_never_consults_the_claim_set_provider()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }

    /// <summary>
    /// A13: the capability gate runs before content-type and body validation, so a provider that
    /// supports no operation answers operation-unsupported 404 even for a malformed POST body.
    /// </summary>
    [TestFixture]
    public class Given_A_Capability_Gate_That_Rejects_Every_Operation_With_A_Malformed_Body
        : IdentityPipelineOrderingTests
    {
        private IFrontendResponse _response = null!;

        private static ClaimSet BuildAuthorizingClaimSet(string action) =>
            new(
                ClaimSetName,
                [
                    new ResourceClaim(
                        $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                        action,
                        [
                            new AuthorizationStrategy(
                                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                            ),
                        ]
                    ),
                ]
            );

        [SetUp]
        public async Task Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .Returns(Task.FromResult<IList<ClaimSet>>([BuildAuthorizingClaimSet("Create")]));

            var applicationContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() =>
                    applicationContextProvider.GetApplicationByClientIdAsync(
                        ClientId,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    Task.FromResult<ApplicationContextResult>(
                        new ApplicationContextResult.Success(
                            new ApplicationContext(1, 1, "client-id", Guid.NewGuid(), [], null, [])
                        )
                    )
                );

            var apiService = BuildApiService(
                multiTenancy: false,
                applicationContextProvider,
                claimSetProvider,
                // NoIdentityService.Capabilities == None, so every operation is unsupported.
                new NoIdentityService(),
                A.Fake<IDataStoreProvider>()
            );

            _response = await apiService.IdentityCreate(
                BuildFrontendRequest(bodyParseErrorMessage: "malformed identity body"),
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_404()
        {
            _response.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_returns_a_body()
        {
            _response.Body.Should().NotBeNull();
        }

        [Test]
        public void It_uses_the_operation_not_supported_type()
        {
            _response.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be(IdentityFailureResponse.OperationNotSupportedType);
        }
    }
}
