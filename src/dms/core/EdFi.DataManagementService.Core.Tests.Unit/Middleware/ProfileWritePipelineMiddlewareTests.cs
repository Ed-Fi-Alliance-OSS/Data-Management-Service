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
