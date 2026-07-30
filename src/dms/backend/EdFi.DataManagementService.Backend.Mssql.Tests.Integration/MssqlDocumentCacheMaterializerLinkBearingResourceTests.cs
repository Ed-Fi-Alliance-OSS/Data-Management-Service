// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_DocumentCacheMaterializer_LinkBearingResource
{
    private const long StudentSchoolAssociationDocumentId = 970101;
    private const short SchoolResourceKeyId = 30;

    private string _databaseName = null!;
    private string _connectionString = null!;
    private MappingSet _mappingSet = null!;
    private MaterializedDocumentFixture _fixture = null!;
    private DocumentCacheMaterializationResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "MSSQL connection string not configured."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        _mappingSet = DocumentCacheMaterializerLinkBearingMappingSet.Create(SqlDialect.Mssql);
        _fixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "ordinary-link-bearing-student-school-association"
        );

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await new MaterializedDocumentFixtureSeeder(
                MaterializedDocumentFixtureSqlDialect.Mssql
            ).SeedAsync(connection, _fixture);
        }

        var commandExecutor = new MssqlRelationalCommandExecutor(
            async cancellationToken =>
            {
                var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                return (DbConnection)connection;
            },
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );
        var materializationDataStore = new AmbientDocumentCacheMaterializationDataStore(
            commandExecutor,
            new MssqlTestDocumentHydrator(_connectionString)
        );

        var sut = new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(materializationDataStore),
            new DocumentCacheDescriptorHydrator(materializationDataStore),
            materializationDataStore,
            new RelationalReadMaterializer(
                new DeterministicLinkSlugResolver(),
                Options.Create(new ResourceLinksOptions { Enabled = false }),
                new ServedEtagComposer()
            ),
            new ServedEtagComposer()
        );

        _result = await sut.MaterializeAsync(CreateRequest(_mappingSet));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_materializes_a_FixtureSeeder_link_bearing_resource_cache_projection_from_real_Mssql_hydration()
    {
        if (_result is not DocumentCacheMaterializationResult.Success success)
        {
            throw new InvalidOperationException($"Expected success, got {_result.GetType().Name}.");
        }

        MaterializedDocumentFixtureAssertions.AssertCandidateMatchesFixture(
            new MaterializedDocumentFixtureActualCacheRow(
                success.Candidate.DocumentId,
                success.Candidate.DocumentUuid.Value.ToString("D"),
                success.Candidate.ProjectName,
                success.Candidate.ResourceName,
                success.Candidate.ResourceVersion,
                success.Candidate.ContentVersion,
                success.Candidate.LastModifiedAt,
                success.Candidate.StreamEtag,
                success.Candidate.DocumentJson
            ),
            _fixture
        );
    }

    private static DocumentCacheMaterializationRequest CreateRequest(MappingSet mappingSet) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            StudentSchoolAssociationDocumentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private sealed class MssqlTestDocumentHydrator(string connectionString) : IDocumentHydrator
    {
        public async Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);

            return await HydrationExecutor.ExecuteAsync(
                connection,
                plan,
                keyset,
                SqlDialect.Mssql,
                executionOptions,
                ct
            );
        }
    }

    private sealed class DeterministicLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId)
        {
            if (resourceKeyId != SchoolResourceKeyId)
            {
                throw new InvalidOperationException(
                    $"Expected School ResourceKeyId {SchoolResourceKeyId}, received {resourceKeyId}."
                );
            }

            return new DocumentLinkSlugTriple(
                ProjectEndpointName: "ed-fi",
                EndpointName: "schools",
                ResourceName: "School"
            );
        }
    }
}
