// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_DocumentCacheSourceMetadataReader
{
    private const long ExistingDocumentId = 970001;
    private const long MissingDocumentId = 970002;
    private static readonly Guid DocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private string _databaseName = null!;
    private string _connectionString = null!;
    private DocumentCacheSourceMetadataReadResult _foundResult = null!;
    private DocumentCacheSourceMetadataReadResult _missingResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "MSSQL connection string not configured."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await ExecuteSql(
                connection,
                """
                IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');

                CREATE TABLE dms.Document (
                    DocumentId bigint NOT NULL CONSTRAINT PK_Document PRIMARY KEY,
                    DocumentUuid uniqueidentifier NOT NULL,
                    ResourceKeyId smallint NOT NULL,
                    CreatedByOwnershipTokenId smallint NULL,
                    ContentVersion bigint NOT NULL,
                    IdentityVersion bigint NOT NULL,
                    ContentLastModifiedAt datetimeoffset NOT NULL,
                    IdentityLastModifiedAt datetimeoffset NOT NULL,
                    CreatedAt datetimeoffset NOT NULL
                );

                INSERT INTO dms.Document (
                    DocumentId,
                    DocumentUuid,
                    ResourceKeyId,
                    CreatedByOwnershipTokenId,
                    ContentVersion,
                    IdentityVersion,
                    ContentLastModifiedAt,
                    IdentityLastModifiedAt,
                    CreatedAt
                )
                VALUES (
                    970001,
                    'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
                    11,
                    NULL,
                    222,
                    111,
                    '2026-07-30T14:15:16+00:00',
                    '2026-07-30T14:15:16+00:00',
                    '2026-07-30T14:15:16+00:00'
                );
                """
            );
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
        var sut = new DocumentCacheSourceMetadataReader(
            new AmbientDocumentCacheMaterializationDataStore(commandExecutor)
        );

        _foundResult = await sut.ReadAsync(CreateRequest(ExistingDocumentId));
        _missingResult = await sut.ReadAsync(CreateRequest(MissingDocumentId));
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
    public void It_reads_source_metadata_from_the_canonical_document_row()
    {
        var found = _foundResult.Should().BeOfType<DocumentCacheSourceMetadataReadResult.Found>().Subject;
        var metadata = found
            .Metadata.Should()
            .BeOfType<DocumentCacheResolvedSourceMetadata.OrdinaryResource>()
            .Subject;

        metadata.DocumentId.Should().Be(ExistingDocumentId);
        metadata.DocumentUuid.Should().Be(new DocumentUuid(DocumentGuid));
        metadata.ResourceKeyId.Should().Be(11);
        metadata.ProjectName.Should().Be("Ed-Fi");
        metadata.ResourceName.Should().Be("School");
        metadata.ResourceVersion.Should().Be("1.0");
        metadata.ContentVersion.Should().Be(222);
        metadata.ContentLastModifiedAt.Should().Be(LastModifiedAt);
    }

    [Test]
    public void It_returns_missing_source_when_the_document_row_is_absent()
    {
        _missingResult.Should().BeSameAs(DocumentCacheSourceMetadataReadResult.MissingSource.Instance);
    }

    private static DocumentCacheMaterializationRequest CreateRequest(long documentId) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                CreateMappingSet(),
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            documentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static MappingSet CreateMappingSet()
    {
        var readPlan = CreateReadPlan(SqlDialect.Mssql);
        var resourceKey = new ResourceKeyEntry(11, SchoolResource, "1.0", false);
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            readPlan.Model
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "test-hash",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [resourceKey]
        );

        return new MappingSet(
            new MappingSetKey("test-hash", SqlDialect.Mssql, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                SqlDialect.Mssql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [concreteResourceModel],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short> { [SchoolResource] = 11 },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry> { [11] = resourceKey },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResourceReadPlan CreateReadPlan(SqlDialect dialect)
    {
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new ResourceReadPlan(
            new RelationalResourceModel(
                SchoolResource,
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                rootTable,
                [rootTable],
                [],
                []
            ),
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [new TableReadPlan(rootTable, "select DocumentId")],
            [],
            []
        );
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
