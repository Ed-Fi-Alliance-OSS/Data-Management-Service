// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

internal abstract class ProfileWritePipelineMiddlewareTests
{
    protected static ProfileWritePipelineMiddleware CreateMiddleware() =>
        new(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            new WritePlanEffectiveSchemaRequiredMembersProvider(),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );

    protected static RequestInfo CreateRequestInfo(
        RequestMethod method,
        JsonNode parsedBody,
        MappingSet mappingSet,
        ProfileContext profileContext
    )
    {
        var resourceInfo = CreateResourceInfo();

        return new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/schools",
                Body: parsedBody.ToJsonString(),
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            method,
            No.ServiceProvider
        )
        {
            ParsedBody = parsedBody,
            ResourceInfo = resourceInfo,
            MappingSet = mappingSet,
            ProfileContext = profileContext,
        };
    }

    protected static ResourceInfo CreateResourceInfo() =>
        new(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("School"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

    protected static ProfileContext CreateIncludeAllWriteProfileContext() =>
        new(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "School",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
            ),
            WasExplicitlySpecified: true
        );

    protected static ProfileContext CreateIncludeOnlyNameWriteProfileContext() =>
        new(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "School",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(
                    MemberSelection.IncludeOnly,
                    [new PropertyRule("name")],
                    [],
                    [],
                    []
                )
            ),
            WasExplicitlySpecified: true
        );

    protected static ProfileContext CreateCalendarReferenceWriteProfileContext() =>
        new(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "School",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(
                    MemberSelection.IncludeOnly,
                    [],
                    [
                        new ObjectRule(
                            Name: "calendarReference",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("calendarType")],
                            NestedObjects: null,
                            Collections: null,
                            Extensions: null
                        ),
                    ],
                    [],
                    []
                )
            ),
            WasExplicitlySpecified: true
        );

    protected static ProfileContext CreateItemsWriteProfileContext() =>
        new(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "School",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(
                    MemberSelection.IncludeOnly,
                    [],
                    [],
                    [
                        new CollectionRule(
                            Name: "items",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("itemId"), new PropertyRule("note")],
                            NestedObjects: null,
                            NestedCollections: null,
                            Extensions: null,
                            ItemFilter: null
                        ),
                    ],
                    []
                )
            ),
            WasExplicitlySpecified: true
        );

    protected static MappingSet CreateRequiredRootMemberMappingSet(ResourceInfo resourceInfo)
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), resourceInfo.ResourceName.Value),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceInfo.ResourceName.Value}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn(
                    "SchoolId",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    CreatePath("$.schoolId", new JsonPathSegment.Property("schoolId"))
                ),
                CreateColumn(
                    "Name",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    true,
                    CreatePath("$.name", new JsonPathSegment.Property("name"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var resourceModel = new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var writePlan = new ResourceWritePlan(
            resourceModel,
            [
                new TableWritePlan(
                    TableModel: rootTable,
                    InsertSql: $"insert into edfi.\"{resourceInfo.ResourceName.Value}\" values (...)",
                    UpdateSql: $"update edfi.\"{resourceInfo.ResourceName.Value}\" set ...",
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            rootTable.Columns[0],
                            new WriteValueSource.DocumentId(),
                            "DocumentId"
                        ),
                        new WriteColumnBinding(
                            rootTable.Columns[1],
                            new WriteValueSource.Scalar(
                                CreatePath("$.schoolId", new JsonPathSegment.Property("schoolId")),
                                new RelationalScalarType(ScalarKind.Int32)
                            ),
                            "SchoolId"
                        ),
                        new WriteColumnBinding(
                            rootTable.Columns[2],
                            new WriteValueSource.Scalar(
                                CreatePath("$.name", new JsonPathSegment.Property("name")),
                                new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                            ),
                            "Name"
                        ),
                    ],
                    KeyUnificationPlans: []
                ),
            ]
        );
        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo("1.0", "v1", "profile-write-test", 0, [], [], []),
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(
                    new ResourceKeyEntry(1, resource, resourceInfo.ResourceVersion.Value, false),
                    resourceModel.StorageKind,
                    resourceModel
                ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey("profile-write-test", SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    protected static MappingSet CreateRequiredCalendarReferenceMappingSet(ResourceInfo resourceInfo)
    {
        var rootPlan = CreateMinimalRootTablePlan(resourceInfo);
        var calendarReferenceTable = new DbTableModel(
            Table: new DbTableName(
                new DbSchemaName("edfi"),
                $"{resourceInfo.ResourceName.Value}CalendarReference"
            ),
            JsonScope: CreatePath("$.calendarReference", new JsonPathSegment.Property("calendarReference")),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceInfo.ResourceName.Value}CalendarReference",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn(
                    "CalendarCode",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    false,
                    CreatePath("$.calendarCode", new JsonPathSegment.Property("calendarCode"))
                ),
                CreateColumn(
                    "CalendarType",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    true,
                    CreatePath("$.calendarType", new JsonPathSegment.Property("calendarType"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var calendarReferencePlan = new TableWritePlan(
            TableModel: calendarReferenceTable,
            InsertSql: $"insert into edfi.\"{resourceInfo.ResourceName.Value}CalendarReference\" values (...)",
            UpdateSql: $"update edfi.\"{resourceInfo.ResourceName.Value}CalendarReference\" set ...",
            DeleteByParentSql: $"delete from edfi.\"{resourceInfo.ResourceName.Value}CalendarReference\" where ...",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, calendarReferenceTable.Columns.Count, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    calendarReferenceTable.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    calendarReferenceTable.Columns[1],
                    new WriteValueSource.Scalar(
                        CreatePath("$.calendarCode", new JsonPathSegment.Property("calendarCode")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "CalendarCode"
                ),
                new WriteColumnBinding(
                    calendarReferenceTable.Columns[2],
                    new WriteValueSource.Scalar(
                        CreatePath("$.calendarType", new JsonPathSegment.Property("calendarType")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "CalendarType"
                ),
            ],
            KeyUnificationPlans: []
        );

        return CreateMappingSet(resourceInfo, rootPlan, calendarReferencePlan);
    }

    protected static MappingSet CreateRequiredCollectionItemMappingSet(ResourceInfo resourceInfo)
    {
        var rootPlan = CreateMinimalRootTablePlan(resourceInfo);
        var itemsTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), $"{resourceInfo.ResourceName.Value}Item"),
            JsonScope: CreatePath(
                "$.items[*]",
                new JsonPathSegment.Property("items"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceInfo.ResourceName.Value}Item",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, false),
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, null, false),
                CreateColumn(
                    "ItemId",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    false,
                    CreatePath("$.itemId", new JsonPathSegment.Property("itemId"))
                ),
                CreateColumn(
                    "Note",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    true,
                    CreatePath("$.note", new JsonPathSegment.Property("note"))
                ),
                CreateColumn(
                    "HiddenField",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    false,
                    CreatePath("$.hiddenField", new JsonPathSegment.Property("hiddenField"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        CreatePath("$.itemId", new JsonPathSegment.Property("itemId")),
                        new DbColumnName("ItemId")
                    ),
                ]
            ),
        };

        var itemsPlan = new TableWritePlan(
            TableModel: itemsTable,
            InsertSql: $"insert into edfi.\"{resourceInfo.ResourceName.Value}Item\" values (...)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, itemsTable.Columns.Count, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    itemsTable.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    itemsTable.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(itemsTable.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    itemsTable.Columns[3],
                    new WriteValueSource.Scalar(
                        CreatePath("$.itemId", new JsonPathSegment.Property("itemId")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ItemId"
                ),
                new WriteColumnBinding(
                    itemsTable.Columns[4],
                    new WriteValueSource.Scalar(
                        CreatePath("$.note", new JsonPathSegment.Property("note")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "Note"
                ),
                new WriteColumnBinding(
                    itemsTable.Columns[5],
                    new WriteValueSource.Scalar(
                        CreatePath("$.hiddenField", new JsonPathSegment.Property("hiddenField")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "HiddenField"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        CreatePath("$.itemId", new JsonPathSegment.Property("itemId")),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: $"update edfi.\"{resourceInfo.ResourceName.Value}Item\" set ...",
                DeleteByStableRowIdentitySql: $"delete from edfi.\"{resourceInfo.ResourceName.Value}Item\" where ...",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        return CreateMappingSet(resourceInfo, rootPlan, itemsPlan);
    }

    private static TableWritePlan CreateMinimalRootTablePlan(ResourceInfo resourceInfo)
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), resourceInfo.ResourceName.Value),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceInfo.ResourceName.Value}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, false)],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: rootTable,
            InsertSql: $"insert into edfi.\"{resourceInfo.ResourceName.Value}\" values (...)",
            UpdateSql: $"update edfi.\"{resourceInfo.ResourceName.Value}\" set ...",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, rootTable.Columns.Count, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(rootTable.Columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    private static MappingSet CreateMappingSet(ResourceInfo resourceInfo, params TableWritePlan[] tablePlans)
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );
        var resourceModel = new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tablePlans[0].TableModel,
            TablesInDependencyOrder: [.. tablePlans.Select(tablePlan => tablePlan.TableModel)],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var writePlan = new ResourceWritePlan(resourceModel, tablePlans);
        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo("1.0", "v1", "profile-write-test", 0, [], [], []),
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(
                    new ResourceKeyEntry(1, resource, resourceInfo.ResourceVersion.Value, false),
                    resourceModel.StorageKind,
                    resourceModel
                ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey("profile-write-test", SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static DbColumnModel CreateColumn(
        string columnName,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        bool isNullable,
        JsonPathExpression? sourceJsonPath = null
    ) =>
        new(
            ColumnName: new DbColumnName(columnName),
            Kind: kind,
            ScalarType: scalarType,
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null
        );

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);
}

[TestFixture]
[Parallelizable]
internal class Given_A_Writable_Profile_Post_Request_With_A_Missing_Write_Plan
    : ProfileWritePipelineMiddlewareTests
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;

    [SetUp]
    public async Task Setup()
    {
        var resourceInfo = CreateResourceInfo();
        _requestInfo = CreateRequestInfo(
            RequestMethod.POST,
            JsonNode.Parse("""{"schoolId":255901}""")!,
            ExtractDocumentInfoMiddlewareTests.CreateMappingSetWithoutWritePlan(resourceInfo),
            CreateIncludeAllWriteProfileContext()
        );
        _nextCalled = false;

        await CreateMiddleware()
            .Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
    }

    [Test]
    public void It_calls_next_so_repository_guard_rails_can_classify_the_failure() =>
        _nextCalled.Should().BeTrue();

    [Test]
    public void It_does_not_attach_a_backend_profile_write_context() =>
        _requestInfo.BackendProfileWriteContext.Should().BeNull();

    [Test]
    public void It_does_not_set_a_frontend_error_response() =>
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
}

[TestFixture]
[Parallelizable]
internal class Given_A_Writable_Profile_Post_Request_That_Must_Defer_Creatability_Until_Target_Resolution
    : ProfileWritePipelineMiddlewareTests
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;
    private ResolvedProfileWriteResult _resolvedCreateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo(
            RequestMethod.POST,
            JsonNode.Parse("""{"name":"Lincoln High"}""")!,
            CreateRequiredRootMemberMappingSet(CreateResourceInfo()),
            CreateIncludeOnlyNameWriteProfileContext()
        );
        _nextCalled = false;

        await CreateMiddleware()
            .Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        _resolvedCreateResult = _requestInfo.BackendProfileWriteContext!.ResolvedProfileWriteInvoker.Execute(
            storedDocument: null,
            isCreate: true,
            scopeCatalog: _requestInfo.BackendProfileWriteContext.CompiledScopeCatalog
        );
    }

    [Test]
    public void It_calls_next() => _nextCalled.Should().BeTrue();

    [Test]
    public void It_does_not_emit_a_frontend_creatability_response() =>
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);

    [Test]
    public void It_attaches_a_backend_profile_write_context() =>
        _requestInfo.BackendProfileWriteContext.Should().NotBeNull();

    [Test]
    public void It_carries_the_pre_resolved_request_body() =>
        _requestInfo
            .BackendProfileWriteContext!.PreResolvedRequest.WritableRequestBody.ToJsonString()
            .Should()
            .Be("""{"name":"Lincoln High"}""");

    [Test]
    public void It_resolves_runtime_required_members_through_the_captured_callback()
    {
        _resolvedCreateResult.Failures.Should().NotBeEmpty();
        _resolvedCreateResult
            .Failures.Should()
            .ContainSingle(failure => failure.Category == ProfileFailureCategory.CreatabilityViolation);
    }
}

[TestFixture]
[Parallelizable]
internal class Given_A_Writable_Profile_Post_Request_With_Forbidden_Submitted_Data
    : ProfileWritePipelineMiddlewareTests
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo(
            RequestMethod.POST,
            JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            CreateRequiredRootMemberMappingSet(CreateResourceInfo()),
            CreateIncludeOnlyNameWriteProfileContext()
        );
        _nextCalled = false;

        await CreateMiddleware()
            .Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
    }

    [Test]
    public void It_does_not_call_next() => _nextCalled.Should().BeFalse();

    [Test]
    public void It_returns_a_frontend_validation_response()
    {
        _requestInfo.FrontendResponse.Should().NotBeSameAs(No.FrontendResponse);
        _requestInfo.FrontendResponse!.StatusCode.Should().Be(400);
    }

    [Test]
    public void It_does_not_attach_a_backend_profile_write_context() =>
        _requestInfo.BackendProfileWriteContext.Should().BeNull();
}

[TestFixture]
[Parallelizable]
internal class Given_A_Writable_Profile_Put_Request_With_A_Hidden_Required_Root_Member
    : ProfileWritePipelineMiddlewareTests
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;
    private ResolvedProfileWriteResult _resolvedUpdateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo(
            RequestMethod.PUT,
            JsonNode.Parse("""{"name":"Lincoln High"}""")!,
            CreateRequiredRootMemberMappingSet(CreateResourceInfo()),
            CreateIncludeOnlyNameWriteProfileContext()
        );
        _nextCalled = false;

        await CreateMiddleware()
            .Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        _resolvedUpdateResult = _requestInfo.BackendProfileWriteContext!.ResolvedProfileWriteInvoker.Execute(
            storedDocument: null,
            isCreate: false,
            scopeCatalog: _requestInfo.BackendProfileWriteContext.CompiledScopeCatalog
        );
    }

    [Test]
    public void It_allows_the_update_flow_to_continue() => _nextCalled.Should().BeTrue();

    [Test]
    public void It_does_not_emit_a_frontend_error_response() =>
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);

    [Test]
    public void It_keeps_resolved_target_execution_valid_for_updates() =>
        _resolvedUpdateResult.Failures.Should().BeEmpty();
}

[TestFixture]
[Parallelizable]
internal class Given_A_Writable_Profile_Put_Request_With_An_Existing_NonRoot_Scope_Whose_Required_Members_Are_Hidden
    : ProfileWritePipelineMiddlewareTests
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;
    private ResolvedProfileWriteResult _resolvedUpdateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo(
            RequestMethod.PUT,
            JsonNode.Parse("""{"calendarReference":{"calendarType":"Main"}}""")!,
            CreateRequiredCalendarReferenceMappingSet(CreateResourceInfo()),
            CreateCalendarReferenceWriteProfileContext()
        );
        _nextCalled = false;

        await CreateMiddleware()
            .Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        _resolvedUpdateResult = _requestInfo.BackendProfileWriteContext!.ResolvedProfileWriteInvoker.Execute(
            storedDocument: JsonNode.Parse(
                """{"calendarReference":{"calendarCode":"2024-01","calendarType":"Main"}}"""
            )!,
            isCreate: false,
            scopeCatalog: _requestInfo.BackendProfileWriteContext.CompiledScopeCatalog
        );
    }

    [Test]
    public void It_allows_the_request_to_continue() => _nextCalled.Should().BeTrue();

    [Test]
    public void It_does_not_emit_a_frontend_error_response() =>
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);

    [Test]
    public void It_attaches_a_backend_profile_write_context() =>
        _requestInfo.BackendProfileWriteContext.Should().NotBeNull();

    [Test]
    public void It_treats_the_existing_scope_as_an_update_once_stored_state_is_available()
    {
        _resolvedUpdateResult.Failures.Should().BeEmpty();
        _resolvedUpdateResult
            .Request!.RequestScopeStates.Should()
            .Contain(state => state.Address.JsonScope == "$.calendarReference" && !state.Creatable);
    }
}

[TestFixture]
[Parallelizable]
internal class Given_A_Writable_Profile_Put_Request_With_An_Existing_Collection_Item_Whose_Required_Members_Are_Hidden
    : ProfileWritePipelineMiddlewareTests
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;
    private ResolvedProfileWriteResult _resolvedUpdateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo(
            RequestMethod.PUT,
            JsonNode.Parse("""{"items":[{"itemId":"Item1","note":"Visible"}]}""")!,
            CreateRequiredCollectionItemMappingSet(CreateResourceInfo()),
            CreateItemsWriteProfileContext()
        );
        _nextCalled = false;

        await CreateMiddleware()
            .Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        _resolvedUpdateResult = _requestInfo.BackendProfileWriteContext!.ResolvedProfileWriteInvoker.Execute(
            storedDocument: JsonNode.Parse(
                """{"items":[{"itemId":"Item1","note":"Visible","hiddenField":"Stored"}]}"""
            )!,
            isCreate: false,
            scopeCatalog: _requestInfo.BackendProfileWriteContext.CompiledScopeCatalog
        );
    }

    [Test]
    public void It_allows_the_request_to_continue() => _nextCalled.Should().BeTrue();

    [Test]
    public void It_does_not_emit_a_frontend_error_response() =>
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);

    [Test]
    public void It_attaches_a_backend_profile_write_context() =>
        _requestInfo.BackendProfileWriteContext.Should().NotBeNull();

    [Test]
    public void It_treats_the_existing_collection_item_as_an_update_once_stored_state_is_available()
    {
        _resolvedUpdateResult.Failures.Should().BeEmpty();
        _resolvedUpdateResult
            .Request!.VisibleRequestCollectionItems.Should()
            .ContainSingle(item => item.Address.JsonScope == "$.items[*]" && !item.Creatable);
    }
}
