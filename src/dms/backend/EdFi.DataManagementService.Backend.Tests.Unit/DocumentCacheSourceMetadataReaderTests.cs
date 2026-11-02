// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheSourceMetadataReader
{
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly Guid DocumentGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);

    [Test]
    public async Task It_returns_missing_source_when_the_canonical_document_row_is_absent()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        var result = await sut.ReadAsync(CreateRequest(CreateMappingSetWithReadPlan()));

        result.Should().BeSameAs(DocumentCacheSourceMetadataReadResult.MissingSource.Instance);
        dataStore.Commands.Should().ContainSingle();
        dataStore.Commands[0].CommandText.Should().Contain("""FROM dms."Document" document""");
        dataStore.Commands[0].CommandText.Should().Contain("""WHERE document."DocumentId" = @documentId""");
        dataStore.Commands[0].CommandText.Should().NotContain("dms.\"ResourceKey\"");
        dataStore.Commands[0].CommandText.Should().NotContain("DocumentProjectionWork");
        dataStore.Commands[0].CommandText.Should().NotContain("DocumentCache");
        dataStore
            .Commands[0]
            .Parameters.Should()
            .ContainSingle(parameter => parameter.Name == "@documentId" && (long)parameter.Value! == 123L);
    }

    [Test]
    public async Task It_resolves_ordinary_resource_metadata_with_the_selected_read_plan()
    {
        var mappingSet = CreateMappingSetWithReadPlan();
        var readPlan = mappingSet.ReadPlansByResource[SchoolResource];
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 11)]),
            new InMemoryRelationalCommandExecution([CreateOwnerRow()]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        var result = await sut.ReadAsync(CreateRequest(mappingSet));

        var found = result.Should().BeOfType<DocumentCacheSourceMetadataReadResult.Found>().Subject;
        var metadata = found
            .Metadata.Should()
            .BeOfType<DocumentCacheResolvedSourceMetadata.OrdinaryResource>()
            .Subject;
        metadata.DocumentId.Should().Be(123);
        metadata.DocumentUuid.Should().Be(new DocumentUuid(DocumentGuid));
        metadata.ResourceKeyId.Should().Be(11);
        metadata.ResourceKey.Resource.Should().Be(SchoolResource);
        metadata.ResourceKey.ResourceVersion.Should().Be("1.0");
        metadata.ProjectName.Should().Be("Ed-Fi");
        metadata.ResourceName.Should().Be("School");
        metadata.ResourceVersion.Should().Be("1.0");
        metadata.ConcreteResourceModel.StorageKind.Should().Be(ResourceStorageKind.RelationalTables);
        metadata.ContentVersion.Should().Be(987);
        metadata.ContentLastModifiedAt.Should().Be(LastModifiedAt);
        metadata.ReadPlan.Should().BeSameAs(readPlan);
        dataStore.Commands.Should().HaveCount(2);
        dataStore.Commands[0].CommandText.Should().NotContain("ContentLastModifiedAt");
        dataStore.Commands[1].CommandText.Should().Contain("""FROM "edfi"."School" owner""");
        dataStore.Commands[1].CommandText.Should().Contain("""owner."DocumentId" = @documentId""");
    }

    [Test]
    public async Task It_accepts_read_plan_table_models_that_omit_hydration_excluded_mirror_columns()
    {
        MappingSet mappingSet = CreateMappingSet();
        ConcreteResourceModel concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(
            SchoolResource
        );
        RelationalResourceModel fullModel = concreteResourceModel.RelationalModel;
        DbTableModel fullRoot = fullModel.Root with
        {
            Columns =
            [
                .. fullModel.Root.Columns,
                CreateMirrorColumn("ContentVersion", ColumnKind.MirroredContentVersion),
                CreateMirrorColumn("ContentLastModifiedAt", ColumnKind.AuthoritativeContentLastModifiedAt),
                CreateMirrorColumn("ContentCreatedAt", ColumnKind.AuthoritativeContentCreatedAt),
            ],
        };
        fullModel = fullModel with { Root = fullRoot, TablesInDependencyOrder = [fullRoot] };
        DbTableModel hydrationRoot = fullRoot with
        {
            Columns =
            [
                .. fullRoot.Columns.Where(column =>
                    column.Kind
                        is not (
                            ColumnKind.MirroredContentVersion
                            or ColumnKind.AuthoritativeContentLastModifiedAt
                            or ColumnKind.AuthoritativeContentCreatedAt
                        )
                ),
            ],
        };
        ResourceReadPlan readPlan = new(
            fullModel,
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new TableReadPlan(hydrationRoot, "select DocumentId")],
            [],
            []
        );
        ConcreteResourceModel updatedConcreteResourceModel = concreteResourceModel with
        {
            RelationalModel = fullModel,
        };
        mappingSet = mappingSet with
        {
            Model = mappingSet.Model with
            {
                ConcreteResourcesInNameOrder =
                [
                    .. mappingSet.Model.ConcreteResourcesInNameOrder.Select(resourceModel =>
                        resourceModel.ResourceKey.Resource == SchoolResource
                            ? updatedConcreteResourceModel
                            : resourceModel
                    ),
                ],
            },
            ReadPlansByResource = new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = readPlan,
            },
        };
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 11)]),
            new InMemoryRelationalCommandExecution([CreateOwnerRow()]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        var result = await sut.ReadAsync(CreateRequest(mappingSet));

        var found = result.Should().BeOfType<DocumentCacheSourceMetadataReadResult.Found>().Subject;
        var metadata = found
            .Metadata.Should()
            .BeOfType<DocumentCacheResolvedSourceMetadata.OrdinaryResource>()
            .Subject;
        metadata.ReadPlan.Should().BeSameAs(readPlan);
    }

    [Test]
    public async Task It_reads_current_source_metadata_without_resolving_the_resource_key_against_the_mapping_set()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 99)]),
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 99)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        var result = await sut.ReadCurrentAsync(CreateRequest(CreateMappingSet()));

        var found = result.Should().BeOfType<DocumentCacheCurrentSourceMetadataReadResult.Found>().Subject;
        found.Metadata.DocumentId.Should().Be(123);
        found.Metadata.DocumentUuid.Should().Be(new DocumentUuid(DocumentGuid));
        found.Metadata.ResourceKeyId.Should().Be(99);
        found.Metadata.ContentVersion.Should().Be(987);
        found.Metadata.ContentLastModifiedAt.Should().BeNull();
        dataStore.Commands.Should().HaveCount(2);
        dataStore.Commands[1].CommandText.Should().Be(dataStore.Commands[0].CommandText);
    }

    [Test]
    public async Task It_resolves_descriptor_resource_metadata_without_requiring_an_ordinary_read_plan()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 13)]),
            new InMemoryRelationalCommandExecution([CreateOwnerRow(descriptorId: 7)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        var result = await sut.ReadAsync(CreateRequest(CreateMappingSet()));

        var found = result.Should().BeOfType<DocumentCacheSourceMetadataReadResult.Found>().Subject;
        var metadata = found
            .Metadata.Should()
            .BeOfType<DocumentCacheResolvedSourceMetadata.DescriptorResource>()
            .Subject;
        metadata.ResourceKey.Resource.Should().Be(DescriptorResource);
        metadata.ConcreteResourceModel.StorageKind.Should().Be(ResourceStorageKind.SharedDescriptorTable);
        metadata.ContentLastModifiedAt.Should().Be(LastModifiedAt);
        dataStore.Commands[1].CommandText.Should().Contain("""FROM "dms"."Descriptor" owner""");
        dataStore.Commands[1].CommandText.Should().Contain("""owner."DocumentId" = @documentId""");
        dataStore.Commands[1].CommandText.Should().NotContain("DescriptorId");
    }

    [Test]
    public async Task It_uses_sql_server_document_metadata_sql_for_sql_server_mapping_sets()
    {
        var mappingSet = CreateMappingSet(dialect: SqlDialect.Mssql) with
        {
            Key = new MappingSetKey("test-hash", SqlDialect.Mssql, "v1"),
        };
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore(
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])],
            SqlDialect.Mssql
        );
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        await sut.ReadAsync(CreateRequest(mappingSet));

        dataStore.Commands.Should().ContainSingle();
        dataStore.Commands[0].CommandText.Should().Contain("FROM [dms].[Document] document");
        dataStore.Commands[0].CommandText.Should().Contain("WHERE document.[DocumentId] = @documentId");
        dataStore.Commands[0].CommandText.Should().NotContain("[dms].[ResourceKey]");
    }

    [TestCase(SqlDialect.Pgsql, false)]
    [TestCase(SqlDialect.Pgsql, true)]
    [TestCase(SqlDialect.Mssql, false)]
    [TestCase(SqlDialect.Mssql, true)]
    public async Task It_preserves_document_metadata_when_the_authoritative_owner_is_absent(
        SqlDialect dialect,
        bool isDescriptor
    )
    {
        var mappingSet = isDescriptor ? CreateMappingSet(dialect) : CreateMappingSetWithReadPlan(dialect);
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore(
            [
                new InMemoryRelationalCommandExecution([
                    CreateSourceRow(isDescriptor ? (short)13 : (short)11),
                ]),
                new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
                new InMemoryRelationalCommandExecution([
                    CreateSourceRow(isDescriptor ? (short)13 : (short)11),
                ]),
                new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
                new InMemoryRelationalCommandExecution([
                    CreateSourceRow(isDescriptor ? (short)13 : (short)11),
                ]),
            ],
            dialect
        );
        var sut = new DocumentCacheSourceMetadataReader(dataStore);
        var request = CreateRequest(mappingSet);

        var initial = await sut.ReadAsync(request);
        var current = await sut.ReadCurrentAsync(request);

        var missingOwner = initial
            .Should()
            .BeOfType<DocumentCacheSourceMetadataReadResult.OwnerMissing>()
            .Subject;
        missingOwner.Metadata.DocumentId.Should().Be(123);
        missingOwner.Metadata.ContentVersion.Should().Be(987);
        missingOwner.Metadata.ContentLastModifiedAt.Should().BeNull();
        var found = current.Should().BeOfType<DocumentCacheCurrentSourceMetadataReadResult.Found>().Subject;
        found.Metadata.Should().Be(missingOwner.Metadata);
        dataStore.Commands.Should().HaveCount(5);
        dataStore.Commands[0].CommandText.Should().NotContain("ContentLastModifiedAt");
        dataStore.Commands[2].CommandText.Should().NotContain("ContentLastModifiedAt");
        dataStore.Commands[4].CommandText.Should().Be(dataStore.Commands[2].CommandText);
        var expectedTable = (dialect, isDescriptor) switch
        {
            (SqlDialect.Pgsql, true) => "\"dms\".\"Descriptor\"",
            (SqlDialect.Pgsql, false) => "\"edfi\".\"School\"",
            (SqlDialect.Mssql, true) => "[dms].[Descriptor]",
            _ => "[edfi].[School]",
        };
        dataStore.Commands[1].CommandText.Should().Contain($"FROM {expectedTable} owner");
        dataStore.Commands[3].CommandText.Should().Contain($"FROM {expectedTable} owner");
        dataStore
            .Commands[1]
            .CommandText.Should()
            .Contain(
                dialect == SqlDialect.Pgsql
                    ? """owner."DocumentId" = @documentId"""
                    : "owner.[DocumentId] = @documentId"
            );
    }

    [TestCase(SqlDialect.Pgsql, false, "Present")]
    [TestCase(SqlDialect.Pgsql, false, "Deleted")]
    [TestCase(SqlDialect.Pgsql, false, "Changed")]
    [TestCase(SqlDialect.Pgsql, true, "Present")]
    [TestCase(SqlDialect.Pgsql, true, "Deleted")]
    [TestCase(SqlDialect.Pgsql, true, "Changed")]
    [TestCase(SqlDialect.Mssql, false, "Present")]
    [TestCase(SqlDialect.Mssql, false, "Deleted")]
    [TestCase(SqlDialect.Mssql, false, "Changed")]
    [TestCase(SqlDialect.Mssql, true, "Present")]
    [TestCase(SqlDialect.Mssql, true, "Deleted")]
    [TestCase(SqlDialect.Mssql, true, "Changed")]
    public async Task It_refreshes_document_metadata_only_after_a_missing_current_owner(
        SqlDialect dialect,
        bool isDescriptor,
        string finalState
    )
    {
        var mappingSet = isDescriptor ? CreateMappingSet(dialect) : CreateMappingSetWithReadPlan(dialect);
        short resourceKeyId = isDescriptor ? (short)13 : (short)11;
        var executions = new List<InMemoryRelationalCommandExecution>
        {
            new([CreateSourceRow(resourceKeyId)]),
            new([
                finalState == "Present"
                    ? CreateOwnerRow(descriptorId: 7)
                    : InMemoryRelationalResultSet.Create(),
            ]),
        };
        if (finalState != "Present")
        {
            executions.Add(
                new([
                    finalState == "Deleted"
                        ? InMemoryRelationalResultSet.Create()
                        : CreateSourceRow(resourceKeyId, 988),
                ])
            );
        }
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore(executions, dialect);
        var result = await new DocumentCacheSourceMetadataReader(dataStore).ReadCurrentAsync(
            CreateRequest(mappingSet)
        );

        if (finalState == "Deleted")
        {
            result.Should().BeSameAs(DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance);
        }
        else
        {
            var current = result
                .Should()
                .BeOfType<DocumentCacheCurrentSourceMetadataReadResult.Found>()
                .Subject.Metadata;
            current.ContentVersion.Should().Be(finalState == "Present" ? 987 : 988);
            current.ContentLastModifiedAt.Should().Be(finalState == "Present" ? LastModifiedAt : null);
        }
        dataStore.Commands.Should().HaveCount(finalState == "Present" ? 2 : 3);
        dataStore.Commands[0].CommandText.Should().NotContain("ContentLastModifiedAt");
        dataStore
            .Commands[1]
            .CommandText.Should()
            .Contain("ContentLastModifiedAt")
            .And.NotContain("DescriptorId");
        if (finalState != "Present")
        {
            dataStore.Commands[2].CommandText.Should().Be(dataStore.Commands[0].CommandText);
        }
    }

    [Test]
    public async Task It_throws_target_mapping_failure_when_the_source_resource_key_is_not_in_the_mapping_set()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 99)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        Func<Task> act = () => sut.ReadAsync(CreateRequest(CreateMappingSet()));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheTargetMappingException>()
        ).Subject.Single();
        exception
            .Reason.Should()
            .Be(DocumentCacheTargetMappingFailureReason.ResourceKeyMissingFromMappingSet);
        exception.FailureMetadata.DocumentId.Should().Be(123);
        exception.FailureMetadata.ResourceKeyId.Should().Be(99);
        exception.FailureMetadata.SelectedRequiredContentVersion.Should().Be(456);
        exception.FailureMetadata.ProjectName.Should().BeNull();
    }

    [Test]
    public async Task It_throws_target_mapping_failure_when_an_ordinary_resource_read_plan_is_missing()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 11)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        Func<Task> act = () => sut.ReadAsync(CreateRequest(CreateMappingSet()));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheTargetMappingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ReadPlanMissing);
        exception.FailureMetadata.ResourceKeyId.Should().Be(11);
        exception.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        exception.FailureMetadata.ResourceName.Should().Be("School");
        exception.FailureMetadata.ResourceVersion.Should().Be("1.0");
    }

    [Test]
    public async Task It_throws_target_mapping_failure_when_resource_key_metadata_has_no_concrete_model()
    {
        var resourceKey = new ResourceKeyEntry(
            40,
            new QualifiedResourceName("Ed-Fi", "MissingModel"),
            "1.0",
            false
        );
        var mappingSet = CreateMappingSet() with
        {
            ResourceKeyById = new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
        };
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 40)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        Func<Task> act = () => sut.ReadAsync(CreateRequest(mappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheTargetMappingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ConcreteResourceModelMissing);
        exception.FailureMetadata.ResourceKeyId.Should().Be(40);
        exception.FailureMetadata.ResourceName.Should().Be("MissingModel");
    }

    [Test]
    public async Task It_throws_target_mapping_failure_when_resource_key_metadata_does_not_match_the_concrete_model()
    {
        var resourceKey = new ResourceKeyEntry(41, SchoolResource, "2.0", false);
        var mappingSet = CreateMappingSetWithReadPlan() with
        {
            ResourceKeyById = new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
        };
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 41)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        Func<Task> act = () => sut.ReadAsync(CreateRequest(mappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheTargetMappingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ConcreteResourceModelMismatch);
        exception.FailureMetadata.ResourceKeyId.Should().Be(41);
        exception.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        exception.FailureMetadata.ResourceName.Should().Be("School");
        exception.FailureMetadata.ResourceVersion.Should().Be("2.0");
    }

    [Test]
    public async Task It_throws_target_mapping_failure_when_the_selected_resource_key_entry_is_malformed()
    {
        var resourceKey = new ResourceKeyEntry(12, SchoolResource, "1.0", false);
        var mappingSet = CreateMappingSetWithReadPlan() with
        {
            ResourceKeyById = new Dictionary<short, ResourceKeyEntry> { [11] = resourceKey },
        };
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 11)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        Func<Task> act = () => sut.ReadAsync(CreateRequest(mappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheTargetMappingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ResourceKeyMetadataMismatch);
        exception.FailureMetadata.ResourceKeyId.Should().Be(11);
        exception.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        exception.FailureMetadata.ResourceName.Should().Be("School");
        exception.FailureMetadata.ResourceVersion.Should().Be("1.0");
    }

    [Test]
    public async Task It_throws_target_mapping_failure_when_the_selected_read_plan_metadata_is_malformed()
    {
        var readPlan = CreateReadPlan(new QualifiedResourceName("Ed-Fi", "Student"), SqlDialect.Pgsql);
        var mappingSet = CreateMappingSet() with
        {
            ReadPlansByResource = new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = readPlan,
            },
        };
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 11)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        Func<Task> act = () => sut.ReadAsync(CreateRequest(mappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheTargetMappingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ReadPlanMetadataMismatch);
        exception.FailureMetadata.ResourceKeyId.Should().Be(11);
        exception.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        exception.FailureMetadata.ResourceName.Should().Be("School");
        exception.FailureMetadata.ResourceVersion.Should().Be("1.0");
    }

    [Test]
    public async Task It_throws_target_mapping_failure_when_the_selected_read_plan_model_diverges_from_the_concrete_model()
    {
        var mappingSet = CreateMappingSet();
        var concreteModel = mappingSet.GetConcreteResourceModelOrThrow(SchoolResource).RelationalModel;
        var mismatchedRootTable = concreteModel.Root with
        {
            Table = new DbTableName(concreteModel.Root.Table.Schema, "SchoolFromDifferentModel"),
        };
        var mismatchedReadPlan = CreateReadPlan(
            concreteModel with
            {
                Root = mismatchedRootTable,
                TablesInDependencyOrder = [mismatchedRootTable],
            },
            SqlDialect.Pgsql
        );
        mappingSet = mappingSet with
        {
            ReadPlansByResource = new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = mismatchedReadPlan,
            },
        };
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateSourceRow(resourceKeyId: 11)]),
        ]);
        var sut = new DocumentCacheSourceMetadataReader(dataStore);

        Func<Task> act = () => sut.ReadAsync(CreateRequest(mappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheTargetMappingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ReadPlanMetadataMismatch);
        exception.FailureMetadata.ResourceKeyId.Should().Be(11);
        exception.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        exception.FailureMetadata.ResourceName.Should().Be("School");
        exception.FailureMetadata.ResourceVersion.Should().Be("1.0");
    }

    private static DocumentCacheMaterializationRequest CreateRequest(MappingSet mappingSet) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            documentId: 123,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            CancellationToken.None
        );

    private static MappingSet CreateMappingSetWithReadPlan(SqlDialect dialect = SqlDialect.Pgsql)
    {
        var mappingSet = CreateMappingSet(dialect);
        var readPlan = CreateReadPlan(
            mappingSet.GetConcreteResourceModelOrThrow(SchoolResource).RelationalModel,
            dialect
        );

        return mappingSet with
        {
            ReadPlansByResource = new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = readPlan,
            },
        };
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect = SqlDialect.Pgsql)
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(
            new QualifiedResourceName("Ed-Fi", "Student")
        );

        return mappingSet with
        {
            Key = new MappingSetKey("test-hash", dialect, "v1"),
        };
    }

    private static ResourceReadPlan CreateReadPlan(QualifiedResourceName resource, SqlDialect dialect)
    {
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), resource.ResourceName),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{resource.ResourceName}",
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

        return CreateReadPlan(
            new RelationalResourceModel(
                resource,
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                rootTable,
                [rootTable],
                [],
                []
            ),
            dialect
        );
    }

    private static ResourceReadPlan CreateReadPlan(RelationalResourceModel model, SqlDialect dialect)
    {
        return new ResourceReadPlan(
            model,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [.. model.TablesInDependencyOrder.Select(table => new TableReadPlan(table, "select DocumentId"))],
            [],
            []
        );
    }

    private static DbColumnModel CreateMirrorColumn(string name, ColumnKind kind) =>
        new(
            new DbColumnName(name),
            kind,
            new RelationalScalarType(
                kind == ColumnKind.MirroredContentVersion ? ScalarKind.Int64 : ScalarKind.DateTime
            ),
            false,
            null,
            null
        )
        {
            IsWritable = false,
        };

    private static InMemoryRelationalResultSet CreateSourceRow(
        short resourceKeyId,
        long contentVersion = 987
    ) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["DocumentId"] = 123L,
                ["DocumentUuid"] = DocumentGuid,
                ["ResourceKeyId"] = resourceKeyId,
                ["ContentVersion"] = contentVersion,
            }
        );

    private static InMemoryRelationalResultSet CreateOwnerRow(int? descriptorId = null) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["ContentLastModifiedAt"] = LastModifiedAt,
                ["DescriptorId"] = descriptorId,
            }
        );
}
