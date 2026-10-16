// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
public class Given_Postgresql_DocumentCacheSourceMetadataReader
{
    private const long ExistingDocumentId = 970001;
    private const long MissingDocumentId = 970002;
    private const long MissingOwnerDocumentId = 970003;
    private static readonly Guid DocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private DocumentCacheSourceMetadataReadResult _foundResult = null!;
    private DocumentCacheSourceMetadataReadResult _missingResult = null!;
    private DocumentCacheSourceMetadataReadResult _missingOwnerResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);

        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            await ExecuteSql(
                connection,
                """
                CREATE SCHEMA dms;
                CREATE SCHEMA edfi;

                CREATE TABLE dms."Document" (
                    "DocumentId" bigint PRIMARY KEY,
                    "DocumentUuid" uuid NOT NULL,
                    "ResourceKeyId" smallint NOT NULL,
                    "CreatedByOwnershipTokenId" smallint NULL,
                    "ContentVersion" bigint NOT NULL
                );

                CREATE TABLE edfi."School" (
                    "DocumentId" bigint PRIMARY KEY,
                    "ContentLastModifiedAt" timestamptz NOT NULL
                );

                INSERT INTO dms."Document" (
                    "DocumentId",
                    "DocumentUuid",
                    "ResourceKeyId",
                    "CreatedByOwnershipTokenId",
                    "ContentVersion"
                )
                VALUES (
                    970001,
                    'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
                    11,
                    NULL,
                    222
                ), (
                    970003,
                    'bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee',
                    11,
                    NULL,
                    223
                );

                INSERT INTO edfi."School" ("DocumentId", "ContentLastModifiedAt")
                VALUES (970001, '2026-07-30T14:15:16Z');
                """
            );
        }

        var commandExecutor = new PostgresqlRelationalCommandExecutor(
            async cancellationToken => (DbConnection)await _dataSource.OpenConnectionAsync(cancellationToken),
            NullLogger<PostgresqlRelationalCommandExecutor>.Instance
        );
        var sut = new DocumentCacheSourceMetadataReader(
            new AmbientDocumentCacheMaterializationDataStore(commandExecutor)
        );

        _foundResult = await sut.ReadAsync(CreateRequest(ExistingDocumentId));
        _missingResult = await sut.ReadAsync(CreateRequest(MissingDocumentId));
        _missingOwnerResult = await sut.ReadAsync(CreateRequest(MissingOwnerDocumentId));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_reads_document_version_and_authoritative_root_timestamp()
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

    [Test]
    public void It_preserves_the_document_observation_when_the_root_is_absent()
    {
        var missing = _missingOwnerResult
            .Should()
            .BeOfType<DocumentCacheSourceMetadataReadResult.OwnerMissing>()
            .Subject;
        missing.Metadata.DocumentId.Should().Be(MissingOwnerDocumentId);
        missing.Metadata.ContentVersion.Should().Be(223);
        missing.Metadata.ContentLastModifiedAt.Should().BeNull();
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
        var readPlan = CreateReadPlan(SqlDialect.Pgsql);
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
            new MappingSetKey("test-hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                SqlDialect.Pgsql,
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
                new DbColumnModel(
                    new DbColumnName("ContentLastModifiedAt"),
                    ColumnKind.AuthoritativeContentLastModifiedAt,
                    new RelationalScalarType(ScalarKind.DateTime),
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
            [new TableReadPlan(rootTable with { Columns = [rootTable.Columns[0]] }, "select DocumentId")],
            [],
            []
        );
    }

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
