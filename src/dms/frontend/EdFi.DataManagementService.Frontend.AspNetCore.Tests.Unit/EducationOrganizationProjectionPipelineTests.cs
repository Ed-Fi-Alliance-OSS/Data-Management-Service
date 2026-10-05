// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Claims;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NUnit.Framework;
using SetResult = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The projection pipeline as the real host composes it: the production container, the real
/// <see cref="IApiService"/> and every Core step, with only the external boundaries replaced (token
/// validation, application binding, claim sets, the data-store catalog, the fingerprint row, the
/// mapping set, and the set reader's database work).
/// </summary>
/// <remarks>
/// The recording set reader stands where the provider reader stands, in the request scope, and reads
/// what the provider reader's acquisition reads: the request's effective target, and the PostgreSQL
/// data source the production <see cref="NpgsqlDataSourceProvider"/> leases for it. The store also
/// configures a snapshot and a read replica, and the request asks for the snapshot, so a pipeline that
/// did not pin the primary would be seen reading another database.
/// </remarks>
[TestFixture]
public class Given_A_Projection_Request_Through_The_Production_Pipeline
{
    private const long DataStoreId = 3788;
    private const string PrimaryConnectionString = "Host=primary.invalid;Database=projection_primary";
    private const string SnapshotConnectionString = "Host=snapshot.invalid;Database=projection_snapshot";
    private const string ReplicaConnectionString = "Host=replica.invalid;Database=projection_replica";
    private const string ClaimSetName = "ProjectionPipelineClaimSet";
    private const string ClientId = "projection-pipeline-client";

    private WebApplicationFactory<Program> _factory = null!;
    private readonly ConcurrentQueue<EffectiveDataStoreTarget> _fingerprintTargets = new();
    private readonly ConcurrentQueue<(EffectiveDataStoreTarget Target, string? Database)> _readerTargets =
        new();
    private IFrontendResponse _response = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:Datastore"] = "postgresql" }
                    )
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                services.RemoveAll<IJwtValidationService>();
                services.AddSingleton(CreateJwtValidationService());

                services.RemoveAll<IApplicationContextProvider>();
                services.AddSingleton(CreateApplicationContextProvider());

                services.RemoveAll<IClaimSetProvider>();
                services.AddSingleton(CreateClaimSetProvider());

                services.RemoveAll<IDataStoreProvider>();
                services.AddSingleton(CreateDataStoreProvider());

                services.RemoveAll<IDatabaseFingerprintReader>();
                services.AddSingleton<IDatabaseFingerprintReader>(
                    serviceProvider => new RecordingFingerprintReader(serviceProvider, _fingerprintTargets)
                );

                services.RemoveAll<IMappingSetProvider>();
                services.AddSingleton(CreateMappingSetProvider());

                services.Replace(
                    ServiceDescriptor.Scoped<IEducationOrganizationProjectionSetReader>(
                        serviceProvider => new RecordingSetReader(serviceProvider, _readerTargets)
                    )
                );
            });
        });

        IApiService apiService = _factory.Services.GetRequiredService<IApiService>();

        _response = await apiService.GetEducationOrganizationProjection(
            new FrontendRequest(
                Path: "/management/education-organizations",
                Body: null,
                Form: null,
                Headers: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer projection-pipeline-token",
                    ["Use-Snapshot"] = "true",
                },
                QueryParameters: new Dictionary<string, string>
                {
                    ["dataStoreId"] = DataStoreId.ToString(),
                    ["limit"] = "2",
                },
                TraceId: new TraceId("projection-pipeline"),
                RouteQualifiers: []
            ),
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void It_answers_the_first_page()
    {
        _response.StatusCode.Should().Be(200, _response.Body?.ToJsonString());
        _response.Body!["items"]!
            .AsArray()
            .Select(item => item!["educationOrganizationId"]!.GetValue<long>())
            .Should()
            .Equal(1, 10);
    }

    [Test]
    public void It_reads_the_set_from_the_resolved_store_primary_database()
    {
        _readerTargets.Should().ContainSingle();
        (EffectiveDataStoreTarget target, string? database) = _readerTargets.Single();

        target.Should().Be(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));
        database.Should().Be("projection_primary");
    }

    [Test]
    public void It_validates_the_fingerprint_of_the_primary_database() =>
        _fingerprintTargets
            .Should()
            .Contain(EffectiveDataStoreTarget.Primary(PrimaryConnectionString))
            .And.NotContain(target => target.Kind != EffectiveTargetKind.Primary);

    private static IJwtValidationService CreateJwtValidationService()
    {
        var jwtValidationService = A.Fake<IJwtValidationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", ClientId)], "test"));
        var clientAuthorizations = new ClientAuthorizations(
            TokenId: "projection-pipeline-token-id",
            ClientId: ClientId,
            ClaimSetName: ClaimSetName,
            EducationOrganizationIds: [],
            NamespacePrefixes: [],
            DataStoreIds: []
        );
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult(((ClaimsPrincipal?)principal, (ClientAuthorizations?)clientAuthorizations))
            );
        return jwtValidationService;
    }

    private static IApplicationContextProvider CreateApplicationContextProvider()
    {
        var provider = A.Fake<IApplicationContextProvider>();
        A.CallTo(() =>
                provider.GetApplicationByClientIdAsync(A<string>._, A<string?>._, A<CancellationToken>._)
            )
            .Returns(
                new ApplicationContextResult.Success(
                    new ApplicationContext(
                        Id: 1,
                        ApplicationId: 1,
                        ClientId: ClientId,
                        ClientUuid: Guid.Parse("44444444-4444-4444-8444-444444444444"),
                        DataStoreIds: [],
                        CreatorOwnershipTokenId: null,
                        OwnershipTokenIds: []
                    )
                )
            );
        return provider;
    }

    private static IClaimSetProvider CreateClaimSetProvider()
    {
        var provider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => provider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns(
                (IList<ClaimSet>)
                    [
                        new ClaimSet(
                            ClaimSetName,
                            [
                                new ResourceClaim(
                                    Conventions.EducationOrganizationProjectionServiceClaimUri,
                                    "Read",
                                    [
                                        new AuthorizationStrategy(
                                            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                        ),
                                    ]
                                ),
                            ]
                        ),
                    ]
            );
        return provider;
    }

    private static IDataStoreProvider CreateDataStoreProvider()
    {
        var store = new DataStore(
            DataStoreId,
            "Primary",
            "Projection pipeline store",
            PrimaryConnectionString,
            [],
            RelationalProviderToken.Postgresql,
            RelationalProviderMetadataStatus.Supported,
            [
                new(DataStoreDerivativeType.Snapshot, SnapshotConnectionString),
                new(DataStoreDerivativeType.ReadReplica, ReplicaConnectionString),
            ]
        );

        var provider = A.Fake<IDataStoreProvider>();
        A.CallTo(() => provider.GetById(DataStoreId, A<string?>._)).Returns(store);
        A.CallTo(() => provider.GetAll(A<string?>._)).Returns([store]);
        A.CallTo(() => provider.LoadDataStores(A<string?>._, A<CancellationToken>._)).Returns([store]);
        A.CallTo(() => provider.LoadTenants(A<CancellationToken>._)).Returns(new List<string>());
        A.CallTo(() => provider.IsLoaded(A<string?>._)).Returns(true);
        A.CallTo(() => provider.GetLoadedTenantKeys()).Returns(new List<string> { "" }.AsReadOnly());
        return provider;
    }

    private static IMappingSetProvider CreateMappingSetProvider()
    {
        var provider = A.Fake<IMappingSetProvider>();
        A.CallTo(() => provider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(CreateMappingSet(call.GetArgument<MappingSetKey>(0)!)));
        return provider;
    }

    private static MappingSet CreateMappingSet(MappingSetKey key) =>
        new(
            Key: key,
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: key.RelationalMappingVersion,
                    EffectiveSchemaHash: key.EffectiveSchemaHash,
                    ResourceKeyCount: 0,
                    ResourceKeySeedHash: new byte[32],
                    SchemaComponentsInEndpointOrder: [],
                    ResourceKeysInIdOrder: []
                ),
                Dialect: key.Dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

    /// <summary>
    /// Answers every target with a fingerprint matching the deployment, and records the targets read.
    /// </summary>
    private sealed class RecordingFingerprintReader(
        IServiceProvider serviceProvider,
        ConcurrentQueue<EffectiveDataStoreTarget> targets
    ) : IDatabaseFingerprintReader
    {
        public Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target)
        {
            targets.Enqueue(target);
            string hash = serviceProvider
                .GetRequiredService<IEffectiveSchemaSetProvider>()
                .EffectiveSchemaSet.EffectiveSchema.EffectiveSchemaHash;

            return Task.FromResult<DatabaseFingerprint?>(
                new DatabaseFingerprint("1.0", hash, 0, new byte[32].ToImmutableArray())
            );
        }
    }

    /// <summary>
    /// Stands in for the provider reader inside the request scope and records what its acquisition
    /// would open: the effective target, and the database of the data source the scope's
    /// <see cref="NpgsqlDataSourceProvider"/> leases for it. No connection is opened.
    /// </summary>
    private sealed class RecordingSetReader(
        IServiceProvider scope,
        ConcurrentQueue<(EffectiveDataStoreTarget Target, string? Database)> targets
    ) : IEducationOrganizationProjectionSetReader
    {
        public Task<SetResult> ReadSetAsync(
            EducationOrganizationProjectionSetReadRequest request,
            CancellationToken cancellationToken
        )
        {
            EffectiveDataStoreTarget target = scope
                .GetRequiredService<IDataStoreSelection>()
                .GetEffectiveTarget();
            string? database = new NpgsqlConnectionStringBuilder(
                scope.GetRequiredService<NpgsqlDataSourceProvider>().DataSource.ConnectionString
            ).Database;
            targets.Enqueue((target, database));

            return Task.FromResult<SetResult>(
                new SetResult.Set([
                    new EducationOrganizationProjectionRow(
                        1,
                        "Ed-Fi:StateEducationAgency",
                        "Sea",
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                    new EducationOrganizationProjectionRow(
                        10,
                        "Ed-Fi:EducationServiceCenter",
                        "Esc",
                        null,
                        null,
                        null,
                        null,
                        1
                    ),
                    new EducationOrganizationProjectionRow(
                        100,
                        "Ed-Fi:LocalEducationAgency",
                        "Lea",
                        null,
                        null,
                        null,
                        10,
                        1
                    ),
                ])
            );
        }
    }
}
