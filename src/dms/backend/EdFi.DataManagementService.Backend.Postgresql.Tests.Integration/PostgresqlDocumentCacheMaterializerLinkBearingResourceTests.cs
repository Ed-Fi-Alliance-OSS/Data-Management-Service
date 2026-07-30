// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
public class Given_Postgresql_DocumentCacheMaterializer_LinkBearingResource
{
    private const long StudentSchoolAssociationDocumentId = 970101;
    private const short StudentSchoolAssociationResourceKeyId = 11;
    private const short SchoolResourceKeyId = 30;
    private const long ContentVersion = 222;
    private const int SchoolId = 255901;

    private static readonly Guid StudentSchoolAssociationDocumentGuid = Guid.Parse(
        "aaaaaaaa-bbbb-cccc-dddd-000000000101"
    );
    private static readonly Guid SchoolDocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000201");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName StudentSchoolAssociationResource = new(
        "Ed-Fi",
        "StudentSchoolAssociation"
    );
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private MappingSet _mappingSet = null!;
    private DocumentCacheMaterializationResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);
        _mappingSet = CreateMappingSet(SqlDialect.Pgsql);

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
                    "ContentVersion" bigint NOT NULL,
                    "IdentityVersion" bigint NOT NULL,
                    "ContentLastModifiedAt" timestamptz NOT NULL,
                    "IdentityLastModifiedAt" timestamptz NOT NULL,
                    "CreatedAt" timestamptz NOT NULL
                );

                CREATE TABLE edfi."StudentSchoolAssociation" (
                    "DocumentId" bigint PRIMARY KEY,
                    "School_DocumentId" bigint NOT NULL,
                    "SchoolReference_SchoolId" integer NOT NULL
                );

                INSERT INTO dms."Document" (
                    "DocumentId",
                    "DocumentUuid",
                    "ResourceKeyId",
                    "CreatedByOwnershipTokenId",
                    "ContentVersion",
                    "IdentityVersion",
                    "ContentLastModifiedAt",
                    "IdentityLastModifiedAt",
                    "CreatedAt"
                )
                VALUES
                (
                    970101,
                    'aaaaaaaa-bbbb-cccc-dddd-000000000101',
                    11,
                    NULL,
                    222,
                    111,
                    '2026-07-30T14:15:16Z',
                    '2026-07-30T14:15:16Z',
                    '2026-07-30T14:15:16Z'
                ),
                (
                    970201,
                    'aaaaaaaa-bbbb-cccc-dddd-000000000201',
                    30,
                    NULL,
                    101,
                    101,
                    '2026-07-29T14:15:16Z',
                    '2026-07-29T14:15:16Z',
                    '2026-07-29T14:15:16Z'
                );

                INSERT INTO edfi."StudentSchoolAssociation" (
                    "DocumentId",
                    "School_DocumentId",
                    "SchoolReference_SchoolId"
                )
                VALUES (970101, 970201, 255901);
                """
            );
        }

        var sut = new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(
                new PostgresqlRelationalCommandExecutor(
                    async cancellationToken =>
                        (DbConnection)await _dataSource.OpenConnectionAsync(cancellationToken),
                    NullLogger<PostgresqlRelationalCommandExecutor>.Instance
                )
            ),
            new PostgresqlTestDocumentHydrator(_dataSource),
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
    public void It_materializes_a_link_bearing_resource_cache_projection_from_real_Postgresql_hydration()
    {
        var success = _result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;

        success.Candidate.DocumentId.Should().Be(StudentSchoolAssociationDocumentId);
        success.Candidate.DocumentUuid.Should().Be(new DocumentUuid(StudentSchoolAssociationDocumentGuid));
        success.Candidate.ProjectName.Should().Be("Ed-Fi");
        success.Candidate.ResourceName.Should().Be("StudentSchoolAssociation");
        success.Candidate.ResourceVersion.Should().Be("1.0");
        success.Candidate.ContentVersion.Should().Be(ContentVersion);
        success.Candidate.LastModifiedAt.Should().Be(LastModifiedAt);
        success.Candidate.StreamEtag.Should().Be("222-01234567.j._.l.i");

        var documentJson = success.Candidate.DocumentJson;
        documentJson["id"]!.GetValue<string>().Should().Be(StudentSchoolAssociationDocumentGuid.ToString());
        documentJson["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-07-30T14:15:16Z");
        documentJson.Should().NotContainKey("_etag");

        var schoolReference = documentJson["schoolReference"]!.AsObject();
        schoolReference["schoolId"]!.GetValue<int>().Should().Be(SchoolId);
        var link = schoolReference["link"]!.AsObject();
        link["rel"]!.GetValue<string>().Should().Be("School");
        link["href"]!.GetValue<string>().Should().Be($"/ed-fi/schools/{SchoolDocumentGuid:D}");
    }

    private static DocumentCacheMaterializationRequest CreateRequest(MappingSet mappingSet) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet
            ),
            StudentSchoolAssociationDocumentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var readPlan = CreateReadPlan(dialect);
        var studentSchoolAssociationKey = new ResourceKeyEntry(
            StudentSchoolAssociationResourceKeyId,
            StudentSchoolAssociationResource,
            "1.0",
            false
        );
        var schoolKey = new ResourceKeyEntry(SchoolResourceKeyId, SchoolResource, "1.0", false);
        var studentSchoolAssociationModel = new ConcreteResourceModel(
            studentSchoolAssociationKey,
            ResourceStorageKind.RelationalTables,
            readPlan.Model
        );
        var schoolModel = new ConcreteResourceModel(
            schoolKey,
            ResourceStorageKind.RelationalTables,
            CreateSchoolRelationalModel()
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 2,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [studentSchoolAssociationKey, schoolKey]
        );

        return new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, dialect, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [schoolModel, studentSchoolAssociationModel],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [StudentSchoolAssociationResource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [StudentSchoolAssociationResource] = StudentSchoolAssociationResourceKeyId,
                [SchoolResource] = SchoolResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [StudentSchoolAssociationResourceKeyId] = studentSchoolAssociationKey,
                [SchoolResourceKeyId] = schoolKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResourceReadPlan CreateReadPlan(SqlDialect dialect)
    {
        var rootTable = CreateStudentSchoolAssociationRootTable();
        var schoolReferencePath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );
        var schoolReferenceSchoolIdPath = new JsonPathExpression(
            "$.schoolReference.schoolId",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
        );
        var model = new RelationalResourceModel(
            StudentSchoolAssociationResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: SchoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: schoolReferenceSchoolIdPath,
                            ReferenceJsonPath: schoolReferenceSchoolIdPath,
                            Column: new DbColumnName("SchoolReference_SchoolId")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            model,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [
                new TableReadPlan(
                    rootTable,
                    SelectStudentSchoolAssociationByKeysetSql(dialect),
                    SelectStudentSchoolAssociationBySingleDocumentSql(dialect)
                ),
            ],
            [
                new ReferenceIdentityProjectionTablePlan(
                    rootTable.Table,
                    [
                        new ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: true,
                            ReferenceObjectPath: schoolReferencePath,
                            TargetResource: SchoolResource,
                            FkColumnOrdinal: 1,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ReferenceIdentityProjectionFieldOrdinal(
                                    ReferenceJsonPath: schoolReferenceSchoolIdPath,
                                    ColumnOrdinal: 2,
                                    ScalarType: new RelationalScalarType(ScalarKind.Int32)
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            [],
            new DocumentReferenceLookupPlan(
                SelectByKeysetSql: SelectSchoolReferenceLookupByKeysetSql(dialect),
                ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
                SourcesInOrder:
                [
                    new DocumentReferenceLookupSource(rootTable.Table, new DbColumnName("School_DocumentId")),
                ],
                SelectBySingleDocumentSql: SelectSchoolReferenceLookupBySingleDocumentSql(dialect)
            )
        );
    }

    private static RelationalResourceModel CreateSchoolRelationalModel()
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

        return new RelationalResourceModel(
            SchoolResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );
    }

    private static DbTableModel CreateStudentSchoolAssociationRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentSchoolAssociation",
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
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression(
                        "$.schoolReference",
                        [new JsonPathSegment.Property("schoolReference")]
                    ),
                    SchoolResource
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
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

    private static string SelectStudentSchoolAssociationByKeysetSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    source."DocumentId",
                    source."School_DocumentId",
                    source."SchoolReference_SchoolId"
                FROM edfi."StudentSchoolAssociation" source
                INNER JOIN "page" keyset
                    ON source."DocumentId" = keyset."DocumentId"
                ORDER BY source."DocumentId";
                """,
            SqlDialect.Mssql => """
                SELECT
                    source.[DocumentId],
                    source.[School_DocumentId],
                    source.[SchoolReference_SchoolId]
                FROM [edfi].[StudentSchoolAssociation] source
                INNER JOIN [#page] keyset
                    ON source.[DocumentId] = keyset.[DocumentId]
                ORDER BY source.[DocumentId];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static string? SelectStudentSchoolAssociationBySingleDocumentSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    source."DocumentId",
                    source."School_DocumentId",
                    source."SchoolReference_SchoolId"
                FROM edfi."StudentSchoolAssociation" source
                WHERE source."DocumentId" = @DocumentId
                ORDER BY source."DocumentId";
                """,
            SqlDialect.Mssql => null,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static string SelectSchoolReferenceLookupByKeysetSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT DISTINCT
                    referenced."DocumentId",
                    referenced."DocumentUuid",
                    referenced."ResourceKeyId"
                FROM edfi."StudentSchoolAssociation" source
                INNER JOIN "page" keyset
                    ON source."DocumentId" = keyset."DocumentId"
                INNER JOIN dms."Document" referenced
                    ON referenced."DocumentId" = source."School_DocumentId"
                ORDER BY referenced."DocumentId";
                """,
            SqlDialect.Mssql => """
                SELECT DISTINCT
                    referenced.[DocumentId],
                    referenced.[DocumentUuid],
                    referenced.[ResourceKeyId]
                FROM [edfi].[StudentSchoolAssociation] source
                INNER JOIN [#page] keyset
                    ON source.[DocumentId] = keyset.[DocumentId]
                INNER JOIN [dms].[Document] referenced
                    ON referenced.[DocumentId] = source.[School_DocumentId]
                ORDER BY referenced.[DocumentId];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static string? SelectSchoolReferenceLookupBySingleDocumentSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT DISTINCT
                    referenced."DocumentId",
                    referenced."DocumentUuid",
                    referenced."ResourceKeyId"
                FROM edfi."StudentSchoolAssociation" source
                INNER JOIN dms."Document" referenced
                    ON referenced."DocumentId" = source."School_DocumentId"
                WHERE source."DocumentId" = @DocumentId
                ORDER BY referenced."DocumentId";
                """,
            SqlDialect.Mssql => null,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class PostgresqlTestDocumentHydrator(NpgsqlDataSource dataSource) : IDocumentHydrator
    {
        public async Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);

            return await HydrationExecutor.ExecuteAsync(
                connection,
                plan,
                keyset,
                SqlDialect.Pgsql,
                executionOptions,
                ct
            );
        }
    }

    private sealed class DeterministicLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId)
        {
            resourceKeyId.Should().Be(SchoolResourceKeyId);

            return new DocumentLinkSlugTriple(
                ProjectEndpointName: "ed-fi",
                EndpointName: "schools",
                ResourceName: "School"
            );
        }
    }
}
