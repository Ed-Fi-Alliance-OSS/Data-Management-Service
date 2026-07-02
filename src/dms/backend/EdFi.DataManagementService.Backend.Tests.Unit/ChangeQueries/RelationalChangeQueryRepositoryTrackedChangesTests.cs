// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.ChangeQueries;

[TestFixture]
[Parallelizable]
public class Given_RelationalChangeQueryRepositoryTrackedChanges
{
    private static readonly DbSchemaName _dmsSchema = new("dms");
    private static readonly DbSchemaName _sourceSchema = new("edfi");
    private static readonly DbSchemaName _trackedSchema = new("tracked_changes_edfi");
    private static readonly DbTableName _descriptorTable = new(_dmsSchema, "Descriptor");
    private static readonly DbTableName _schoolTable = new(_sourceSchema, "School");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _programTypeDescriptorResource = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );

    [TestCase(SqlDialect.Pgsql, "SELECT \"dms\".\"GetMaxChangeVersion\"() AS \"NewestChangeVersion\"")]
    [TestCase(SqlDialect.Mssql, "SELECT [dms].[GetMaxChangeVersion]() AS [NewestChangeVersion]")]
    public async Task It_executes_dialect_specific_newest_change_version_sql(
        SqlDialect dialect,
        string expectedCommandText
    )
    {
        var executor = new InMemoryRelationalCommandExecutor(
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        RelationalAccessTestData.CreateRow(("NewestChangeVersion", 99L))
                    ),
                ]),
            ],
            dialect
        );
        var sut = new RelationalChangeQueryRepository(executor, A.Fake<IRelationalParameterConfigurator>());

        var result = await sut.GetNewestChangeVersion();

        result.Should().Be(99L);
        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Be(expectedCommandText);
    }

    [Test]
    public async Task It_returns_empty_keychanges_without_sql_for_descriptors()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var sut = new RelationalChangeQueryRepository(executor, A.Fake<IRelationalParameterConfigurator>());
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: true,
            trackedChangeTable: CreateSharedDescriptorTrackedTable(),
            resourceInfo: CreateResourceInfo(_programTypeDescriptorResource, isDescriptor: true),
            resourceModel: CreateSharedDescriptorResourceModel()
        );

        TrackedChangeQueryResult result = await sut.QueryTrackedChanges(request);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
        executor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_authorization_failure_before_empty_keychanges_for_descriptors()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var sut = new RelationalChangeQueryRepository(executor, A.Fake<IRelationalParameterConfigurator>());
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: true,
            trackedChangeTable: CreateSharedDescriptorTrackedTable(),
            resourceInfo: CreateResourceInfo(_programTypeDescriptorResource, isDescriptor: true),
            resourceModel: CreateSharedDescriptorResourceModel(),
            evaluators: [new AuthorizationStrategyEvaluator("NamespaceBased", [], FilterOperator.Or)],
            authorizationContext: new RelationalAuthorizationContext([])
        );

        TrackedChangeQueryResult result = await sut.QueryTrackedChanges(request);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().BeNull();
        result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.NamespaceNoPrefixesConfigured>();
        executor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_empty_keychanges_without_mapping_fields_or_sql_for_concrete_abstract_resources()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var sut = new RelationalChangeQueryRepository(executor, A.Fake<IRelationalParameterConfigurator>());
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: true,
            trackedChangeTable: CreateConcreteAbstractTrackedTable(),
            resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false),
            resourceModel: CreateConcreteAbstractResourceModelWithoutQueryFieldMappings()
        );

        TrackedChangeQueryResult result = await sut.QueryTrackedChanges(request);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
        executor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_executes_deletes_plan_and_reads_results()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        ("__ChangeVersion", 42L),
                        ("schoolId__old", 255901)
                    )
                ),
            ]),
        ]);
        var sut = new RelationalChangeQueryRepository(executor, A.Fake<IRelationalParameterConfigurator>());
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: CreateSchoolTrackedTable(),
            resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false),
            resourceModel: CreateRegularResourceModel()
        );

        TrackedChangeQueryResult result = await sut.QueryTrackedChanges(request);

        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("tracked_changes_edfi");
        result.Items.Should().ContainSingle();
        JsonObject item = result.Items[0]!.AsObject();
        JsonObject keyValues = item["keyValues"]!.AsObject();
        keyValues["schoolId"]!.GetValue<int>().Should().Be(255901);
    }

    [Test]
    public async Task It_rejects_non_relational_tracked_change_request()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var sut = new RelationalChangeQueryRepository(executor, A.Fake<IRelationalParameterConfigurator>());
        ITrackedChangeQueryRequest request = A.Fake<ITrackedChangeQueryRequest>();

        Func<Task> act = () => sut.QueryTrackedChanges(request);

        await act.Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("Tracked Change Queries require an IRelationalTrackedChangeQueryRequest.");
    }

    [Test]
    public async Task It_returns_security_configuration_failure_for_unsupported_strategy()
    {
        var executor = A.Fake<IRelationalCommandExecutor>();
        A.CallTo(() => executor.Dialect).Returns(SqlDialect.Pgsql);

        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: CreateSchoolTrackedTable(),
            resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false),
            resourceModel: CreateRegularResourceModel(),
            evaluators: [new AuthorizationStrategyEvaluator("OwnershipBased", [], FilterOperator.Or)]
        );

        TrackedChangeQueryResult result = await new RelationalChangeQueryRepository(
            executor,
            A.Fake<IRelationalParameterConfigurator>()
        ).QueryTrackedChanges(request);

        var failure = result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Equal("OwnershipBased");
        failure.Errors.Should().BeEmpty();
        A.CallTo(() =>
                executor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<TrackedChangeQueryResult>>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_security_configuration_when_mssql_authorization_parameters_exceed_command_limit()
    {
        var executor = A.Fake<IRelationalCommandExecutor>();
        A.CallTo(() => executor.Dialect).Returns(SqlDialect.Mssql);
        A.CallTo(() =>
                executor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<TrackedChangeQueryResult>>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(new TrackedChangeQueryResult([], null)));

        ConcreteResourceModel resourceModel = CreateAuthorizedSchoolResourceModel();
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: CreateAuthorizedSchoolTrackedTable(),
            resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false),
            resourceModel: resourceModel,
            evaluators:
            [
                new AuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NamespaceBased,
                    [],
                    FilterOperator.Or
                ),
                new AuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    [],
                    FilterOperator.Or
                ),
            ],
            authorizationContext: new RelationalAuthorizationContext(
                CreateClaimEducationOrganizationIds(1050),
                CreateNamespacePrefixes(1050)
            ),
            dialect: SqlDialect.Mssql
        );

        TrackedChangeQueryResult result = await new RelationalChangeQueryRepository(
            executor,
            A.Fake<IRelationalParameterConfigurator>()
        ).QueryTrackedChanges(request);

        var failure = result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().BeEmpty();
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("exceed the SQL Server parameter limit");
        A.CallTo(() =>
                executor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<TrackedChangeQueryResult>>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_security_configuration_when_mssql_namespace_prefix_count_exceeds_scalar_limit()
    {
        var executor = A.Fake<IRelationalCommandExecutor>();
        A.CallTo(() => executor.Dialect).Returns(SqlDialect.Mssql);

        ConcreteResourceModel resourceModel = CreateAuthorizedSchoolResourceModel();
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: CreateAuthorizedSchoolTrackedTable(),
            resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false),
            resourceModel: resourceModel,
            evaluators:
            [
                new AuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NamespaceBased,
                    [],
                    FilterOperator.Or
                ),
            ],
            authorizationContext: new RelationalAuthorizationContext(
                [],
                CreateNamespacePrefixes(NamespacePrefixLimitExceededException.MssqlScalarParameterLimit)
            ),
            dialect: SqlDialect.Mssql
        );

        TrackedChangeQueryResult result = await new RelationalChangeQueryRepository(
            executor,
            A.Fake<IRelationalParameterConfigurator>()
        ).QueryTrackedChanges(request);

        var failure = result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().BeEmpty();
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                NamespaceAuthorizationSecurityConfigurationMessages.PrefixCapExceeded(
                    NamespacePrefixLimitExceededException.MssqlScalarParameterLimit
                )
            );
        A.CallTo(() =>
                executor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<TrackedChangeQueryResult>>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_security_configuration_when_namespace_prefix_is_empty()
    {
        var executor = A.Fake<IRelationalCommandExecutor>();
        A.CallTo(() => executor.Dialect).Returns(SqlDialect.Pgsql);

        ConcreteResourceModel resourceModel = CreateAuthorizedSchoolResourceModel();
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: CreateAuthorizedSchoolTrackedTable(),
            resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false),
            resourceModel: resourceModel,
            evaluators:
            [
                new AuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NamespaceBased,
                    [],
                    FilterOperator.Or
                ),
            ],
            authorizationContext: new RelationalAuthorizationContext([], [""])
        );

        TrackedChangeQueryResult result = await new RelationalChangeQueryRepository(
            executor,
            A.Fake<IRelationalParameterConfigurator>()
        ).QueryTrackedChanges(request);

        var failure = result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().BeEmpty();
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(NamespaceAuthorizationSecurityConfigurationMessages.InvalidNamespacePrefix);
        A.CallTo(() =>
                executor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<TrackedChangeQueryResult>>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    private static IRelationalTrackedChangeQueryRequest CreateRequest(
        ChangeQueryEndpointOperation operation,
        bool totalCount,
        TrackedChangeTableInfo trackedChangeTable,
        ResourceInfo resourceInfo,
        ConcreteResourceModel resourceModel,
        IReadOnlyList<AuthorizationStrategyEvaluator>? evaluators = null,
        RelationalAuthorizationContext? authorizationContext = null,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var request = A.Fake<IRelationalTrackedChangeQueryRequest>();

        A.CallTo(() => request.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => request.Operation).Returns(operation);
        A.CallTo(() => request.PaginationParameters)
            .Returns(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: totalCount, MaximumPageSize: 500)
            );
        A.CallTo(() => request.ChangeVersionRange).Returns(ChangeVersionRange.None);
        A.CallTo(() => request.TraceId).Returns(new TraceId("tracked-change-repository-test"));
        A.CallTo(() => request.AuthorizationContext)
            .Returns(authorizationContext ?? new RelationalAuthorizationContext([]));
        A.CallTo(() => request.AuthorizationStrategyEvaluators).Returns(evaluators ?? []);
        A.CallTo(() => request.MappingSet)
            .Returns(CreateMappingSet(resourceModel.RelationalModel.Resource, dialect));
        A.CallTo(() => request.ResourceModel).Returns(resourceModel);
        A.CallTo(() => request.TrackedChangeTable).Returns(trackedChangeTable);

        return request;
    }

    private static TrackedChangeTableInfo CreateSchoolTrackedTable()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );

        return CreateTrackedChangeTable(TrackedChangeTableKind.Resource, _schoolTable, [schoolIdColumn]);
    }

    private static TrackedChangeTableInfo CreateAuthorizedSchoolTrackedTable()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        TrackedChangeColumnInfo namespaceColumn = ValueColumn(
            "Namespace",
            "$.namespace",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.SecurableElement,
            canonicalStorageColumn: new DbColumnName("Namespace"),
            scalarKind: ScalarKind.String
        );

        return CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            _schoolTable,
            [schoolIdColumn, namespaceColumn]
        );
    }

    private static TrackedChangeTableInfo CreateConcreteAbstractTrackedTable()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );

        return CreateTrackedChangeTable(
            TrackedChangeTableKind.ConcreteAbstract,
            _schoolTable,
            [schoolIdColumn]
        );
    }

    private static TrackedChangeTableInfo CreateSharedDescriptorTrackedTable()
    {
        TrackedChangeColumnInfo namespaceColumn = ValueColumn(
            "Namespace",
            "$.namespace",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("Namespace"),
            scalarKind: ScalarKind.String
        );
        TrackedChangeColumnInfo codeValueColumn = ValueColumn(
            "CodeValue",
            "$.codeValue",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("CodeValue"),
            scalarKind: ScalarKind.String
        );

        return CreateTrackedChangeTable(
            TrackedChangeTableKind.SharedDescriptor,
            _descriptorTable,
            [namespaceColumn, codeValueColumn]
        );
    }

    private static TrackedChangeTableInfo CreateTrackedChangeTable(
        TrackedChangeTableKind kind,
        DbTableName sourceTable,
        IReadOnlyList<TrackedChangeColumnInfo> valueColumns
    ) =>
        new(
            Table: new DbTableName(
                _trackedSchema,
                kind is TrackedChangeTableKind.SharedDescriptor ? "Descriptor" : "School"
            ),
            Kind: kind,
            SourceTable: sourceTable,
            ValueColumnsInTableOrder: valueColumns,
            SystemColumns: DefaultSystemColumns(kind),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    private static TrackedChangeColumnInfo ValueColumn(
        string columnName,
        string sourceJsonPath,
        TrackedChangeColumnRole role,
        TrackedChangeColumnOrigin origin,
        DbColumnName? canonicalStorageColumn,
        ScalarKind scalarKind = ScalarKind.Int32
    ) =>
        new(
            OldColumnName: new DbColumnName($"Old{columnName}"),
            NewColumnName: new DbColumnName($"New{columnName}"),
            SourceJsonPath: sourceJsonPath,
            CanonicalStorageColumn: canonicalStorageColumn,
            IsOldColumnNullable: false,
            IsNewColumnNullable: true,
            ScalarType: new RelationalScalarType(scalarKind),
            Role: role,
            Origin: origin
        );

    private static IReadOnlyList<TrackedChangeSystemColumnInfo> DefaultSystemColumns(
        TrackedChangeTableKind kind
    )
    {
        List<TrackedChangeSystemColumnInfo> columns =
        [
            SystemColumn(TrackedChangeSystemColumnRole.Id, "Id", scalarType: null),
            SystemColumn(
                TrackedChangeSystemColumnRole.ChangeVersion,
                "ChangeVersion",
                new RelationalScalarType(ScalarKind.Int64)
            ),
            SystemColumn(
                TrackedChangeSystemColumnRole.CreatedAt,
                "CreatedAt",
                new RelationalScalarType(ScalarKind.DateTime)
            ),
        ];

        if (kind is TrackedChangeTableKind.SharedDescriptor)
        {
            columns.Add(
                SystemColumn(
                    TrackedChangeSystemColumnRole.Discriminator,
                    "Discriminator",
                    new RelationalScalarType(ScalarKind.String)
                )
            );
        }

        return columns;
    }

    private static TrackedChangeSystemColumnInfo SystemColumn(
        TrackedChangeSystemColumnRole role,
        string columnName,
        RelationalScalarType? scalarType
    ) => new(role, new DbColumnName(columnName), scalarType, IsNullable: false, IsPrimaryKey: false);

    private static ConcreteResourceModel CreateRegularResourceModel() =>
        CreateResourceModel(
            _schoolResource,
            ResourceStorageKind.RelationalTables,
            _schoolTable,
            [RootColumn("SchoolId", "$.schoolId", ScalarKind.Int32)],
            CreateQueryFieldMappings(("schoolId", "$.schoolId", "integer"))
        );

    private static ConcreteResourceModel CreateAuthorizedSchoolResourceModel()
    {
        ConcreteResourceModel resourceModel = CreateResourceModel(
            _schoolResource,
            ResourceStorageKind.RelationalTables,
            _schoolTable,
            [
                RootColumn("SchoolId", "$.schoolId", ScalarKind.Int32),
                RootColumn("Namespace", "$.namespace", ScalarKind.String),
            ],
            CreateQueryFieldMappings(("schoolId", "$.schoolId", "integer")),
            securableElements: new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolId", "SchoolId")],
                ["$.namespace"],
                [],
                [],
                []
            )
        );
        return resourceModel;
    }

    private static ConcreteResourceModel CreateConcreteAbstractResourceModelWithoutQueryFieldMappings() =>
        CreateResourceModel(
            _schoolResource,
            ResourceStorageKind.RelationalTables,
            _schoolTable,
            [RootColumn("SchoolId", "$.schoolId", ScalarKind.Int32)],
            new Dictionary<string, RelationalQueryFieldMapping>(StringComparer.Ordinal)
        );

    private static ConcreteResourceModel CreateSharedDescriptorResourceModel() =>
        CreateResourceModel(
            _programTypeDescriptorResource,
            ResourceStorageKind.SharedDescriptorTable,
            _descriptorTable,
            [
                RootColumn("Namespace", "$.namespace", ScalarKind.String),
                RootColumn("CodeValue", "$.codeValue", ScalarKind.String),
                RootColumn("Discriminator", sourceJsonPath: null, ScalarKind.String),
            ],
            CreateQueryFieldMappings(
                ("namespace", "$.namespace", "string"),
                ("codeValue", "$.codeValue", "string")
            ),
            new DescriptorMetadata(
                new DescriptorColumnContract(
                    new DbColumnName("Namespace"),
                    new DbColumnName("CodeValue"),
                    null,
                    null,
                    null,
                    null,
                    null
                ),
                DiscriminatorStrategy.ResourceKeyId
            )
        );

    private static ConcreteResourceModel CreateResourceModel(
        QualifiedResourceName resource,
        ResourceStorageKind storageKind,
        DbTableName rootTableName,
        IReadOnlyList<DbColumnModel> rootColumns,
        IReadOnlyDictionary<string, RelationalQueryFieldMapping> queryFieldMappings,
        DescriptorMetadata? descriptorMetadata = null,
        ResourceSecurableElements? securableElements = null
    )
    {
        DbColumnModel documentIdColumn = RootColumn("DocumentId", sourceJsonPath: null, ScalarKind.Int64);
        var rootModel = new DbTableModel(
            rootTableName,
            Path("$"),
            new TableKey($"PK_{rootTableName.Name}", []),
            Columns: [documentIdColumn, .. rootColumns],
            Constraints: []
        );
        var relationalModel = new RelationalResourceModel(
            resource,
            rootTableName.Schema,
            storageKind,
            rootModel,
            TablesInDependencyOrder: [rootModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, resource, ResourceVersion: "1.0", IsAbstractResource: false),
            storageKind,
            relationalModel,
            descriptorMetadata
        )
        {
            QueryFieldMappingsByQueryField = queryFieldMappings,
            SecurableElements = securableElements ?? ResourceSecurableElements.Empty,
        };
    }

    private static DbColumnModel RootColumn(
        string columnName,
        string? sourceJsonPath,
        ScalarKind scalarKind
    ) =>
        new(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(scalarKind),
            IsNullable: false,
            SourceJsonPath: sourceJsonPath is null ? null : Path(sourceJsonPath),
            TargetResource: null
        );

    private static IReadOnlyDictionary<string, RelationalQueryFieldMapping> CreateQueryFieldMappings(
        params (string FieldName, string Path, string Type)[] mappings
    ) =>
        mappings.ToDictionary(
            static mapping => mapping.FieldName,
            static mapping => new RelationalQueryFieldMapping(
                mapping.FieldName,
                [new RelationalQueryFieldPath(Path(mapping.Path), mapping.Type)]
            ),
            StringComparer.Ordinal
        );

    private static MappingSet CreateMappingSet(
        QualifiedResourceName resource,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var resourceKey = new ResourceKeyEntry(
            1,
            resource,
            ResourceVersion: "1.0",
            IsAbstractResource: false
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "5.2",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: new byte[32],
                    SchemaComponentsInEndpointOrder: [],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static IReadOnlyList<long> CreateClaimEducationOrganizationIds(int count) =>
        [.. Enumerable.Range(1, count).Select(static id => (long)id)];

    private static IReadOnlyList<string> CreateNamespacePrefixes(int count) =>
        [.. Enumerable.Range(1, count).Select(static id => $"uri://namespace-{id:D4}/")];

    private static ResourceInfo CreateResourceInfo(QualifiedResourceName resource, bool isDescriptor) =>
        new(
            new ProjectName(resource.ProjectName),
            new ResourceName(resource.ResourceName),
            isDescriptor,
            new SemVer("5.0.0"),
            AllowIdentityUpdates: false
        );
}
