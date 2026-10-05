// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The real host with the production projection pipeline, in which only the external boundaries are
/// replaced: token validation (every bearer token is a client of <see cref="ClaimSetName"/>),
/// application binding, claim sets (that claim set holds the projection service claim), the
/// data-store catalog (one store), the fingerprint row, the mapping set, and the set reader.
/// </summary>
internal static class EducationOrganizationProjectionTestHost
{
    public const string ClaimSetName = "ProjectionPipelineClaimSet";
    public const string ClientId = "projection-pipeline-client";

    /// <param name="store">The one store the catalog holds, in the single-tenant catalog.</param>
    /// <param name="createSetReader">Builds the set reader in each request scope.</param>
    /// <param name="createFingerprintReader">
    /// Builds the fingerprint reader; by default one that matches the deployment on every target.
    /// </param>
    public static WebApplicationFactory<Program> Create(
        DataStore store,
        Func<IServiceProvider, IEducationOrganizationProjectionSetReader> createSetReader,
        Func<IServiceProvider, IDatabaseFingerprintReader>? createFingerprintReader = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                services.AddSingleton(CreateDataStoreProvider(store));

                services.RemoveAll<IDatabaseFingerprintReader>();
                services.AddSingleton(
                    createFingerprintReader
                        ?? (serviceProvider => new MatchingFingerprintReader(serviceProvider, new()))
                );

                services.RemoveAll<IMappingSetProvider>();
                services.AddSingleton(CreateMappingSetProvider());

                services.Replace(
                    ServiceDescriptor.Scoped<IEducationOrganizationProjectionSetReader>(createSetReader)
                );
            });
        });

    /// <summary>
    /// A store of the deployment's dialect, with no route context and no derivatives.
    /// </summary>
    public static DataStore PostgresqlStore(long dataStoreId) =>
        new(
            dataStoreId,
            "Primary",
            "Projection store",
            "Host=primary.invalid;Database=projection_primary",
            [],
            RelationalProviderToken.Postgresql,
            RelationalProviderMetadataStatus.Supported
        );

    /// <summary>
    /// A checked-in contract example, located by walking up from the test output directory to the
    /// repository's design folder.
    /// </summary>
    public static JsonNode ContractExample(string fileName)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "reference",
                "design",
                "edorg-projection-DMS-1440",
                "contract",
                "examples",
                fileName
            );

            if (File.Exists(candidate))
            {
                return JsonNode.Parse(File.ReadAllText(candidate))!;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Contract example '{fileName}' was not found above the test directory."
        );
    }

    /// <summary>
    /// Answers every read with the same rows.
    /// </summary>
    public sealed class FixedSetReader(IReadOnlyList<EducationOrganizationProjectionRow> rows)
        : IEducationOrganizationProjectionSetReader
    {
        public Task<EducationOrganizationProjectionSetResult> ReadSetAsync(
            EducationOrganizationProjectionSetReadRequest request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<EducationOrganizationProjectionSetResult>(
                new EducationOrganizationProjectionSetResult.Set(rows)
            );
    }

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

    private static IDataStoreProvider CreateDataStoreProvider(DataStore store)
    {
        var provider = A.Fake<IDataStoreProvider>();
        A.CallTo(() => provider.GetById(store.Id, A<string?>._)).Returns(store);
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
    public sealed class MatchingFingerprintReader(
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
}
