// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class Given_A_Writable_Profile_Post_Request_With_A_Missing_Write_Plan
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo();
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
    public void It_calls_next_so_repository_guard_rails_can_classify_the_failure()
    {
        _nextCalled.Should().BeTrue();
    }

    [Test]
    public void It_does_not_attach_a_backend_profile_write_context()
    {
        _requestInfo.BackendProfileWriteContext.Should().BeNull();
    }

    [Test]
    public void It_does_not_set_a_frontend_error_response()
    {
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    private static ProfileWritePipelineMiddleware CreateMiddleware()
    {
        return new ProfileWritePipelineMiddleware(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo()
    {
        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("School")
            .WithIdentityJsonPaths(["$.schoolId"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("SchoolId", "$.schoolId")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ProjectSchema projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "schools");
        var resourceInfo = CreateResourceInfo();

        return new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/schools",
                Body: """{"schoolId":255901}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        )
        {
            ParsedBody = JsonNode.Parse("""{"schoolId":255901}""")!,
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchema,
            ResourceInfo = resourceInfo,
            MappingSet = ExtractDocumentInfoMiddlewareTests.CreateMappingSetWithoutWritePlan(resourceInfo),
            ProfileContext = CreateWriteProfileContext(),
        };
    }

    private static ResourceInfo CreateResourceInfo()
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("School"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    private static ProfileContext CreateWriteProfileContext()
    {
        return new ProfileContext(
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
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Writable_Profile_Post_Request_That_May_Resolve_To_An_Existing_Document
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;
    private ProfileAppliedWriteContext _storedStateContext = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo();
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

        _storedStateContext =
            _requestInfo.BackendProfileWriteContext!.StoredStateProjectionInvoker.ProjectStoredState(
                JsonNode.Parse("""{"schoolId":255901}""")!,
                _requestInfo.BackendProfileWriteContext.Request,
                _requestInfo.BackendProfileWriteContext.CompiledScopeCatalog
            );
    }

    [Test]
    public void It_calls_next_so_executor_routing_can_run()
    {
        _nextCalled.Should().BeTrue();
    }

    [Test]
    public void It_does_not_set_a_frontend_error_response_before_target_resolution()
    {
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    [Test]
    public void It_attaches_a_backend_profile_write_context_for_downstream_execution()
    {
        _requestInfo.BackendProfileWriteContext.Should().NotBeNull();
    }

    [Test]
    public void It_can_project_the_same_post_request_as_an_existing_document_update()
    {
        _storedStateContext.Request.RootResourceCreatable.Should().BeFalse();
    }

    [Test]
    public void It_projects_visible_stored_state_for_existing_document_routing()
    {
        JsonNode
            .DeepEquals(_storedStateContext.VisibleStoredBody, JsonNode.Parse("""{"schoolId":255901}"""))
            .Should()
            .BeTrue();
    }

    private static ProfileWritePipelineMiddleware CreateMiddleware()
    {
        return new ProfileWritePipelineMiddleware(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo()
    {
        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("School")
            .WithIdentityJsonPaths(["$.schoolId"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("SchoolId", "$.schoolId")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ProjectSchema projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "schools");
        var resourceInfo = CreateResourceInfo();

        return new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/schools",
                Body: """{"schoolId":255901}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        )
        {
            ParsedBody = JsonNode.Parse("""{"schoolId":255901}""")!,
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchema,
            ResourceInfo = resourceInfo,
            MappingSet = ExtractDocumentInfoMiddlewareTests.CreateMappingSet(resourceInfo),
            ProfileContext = CreateWriteProfileContext(),
        };
    }

    private static ResourceInfo CreateResourceInfo()
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("School"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    private static ProfileContext CreateWriteProfileContext()
    {
        return new ProfileContext(
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
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Writable_Profile_Post_Request_Without_Preseeded_ResourceInfo
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;

    [SetUp]
    public async Task Setup()
    {
        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("School")
            .WithIdentityJsonPaths(["$.schoolId"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("SchoolId", "$.schoolId")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ProjectSchema projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "schools");

        var resourceInfo = new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("School"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

        _requestInfo = new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/schools",
                Body: """{"schoolId":255901}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        )
        {
            ParsedBody = JsonNode.Parse("""{"schoolId":255901}""")!,
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchema,
            // Intentionally leave ResourceInfo as No.ResourceInfo — the middleware
            // runs before BuildResourceInfoMiddleware in the real pipeline.
            MappingSet = ExtractDocumentInfoMiddlewareTests.CreateMappingSet(resourceInfo),
            ProfileContext = CreateWriteProfileContext(),
        };
        _nextCalled = false;

        // Pin the precondition as an assertion: the bug only reproduces when
        // ResourceInfo is left as No.ResourceInfo.
        _requestInfo.ResourceInfo.Should().BeSameAs(No.ResourceInfo);

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
    public void It_resolves_the_write_plan_without_depending_on_preseeded_resource_info()
    {
        _requestInfo.BackendProfileWriteContext.Should().NotBeNull();
    }

    [Test]
    public void It_calls_next_so_downstream_middleware_can_run()
    {
        _nextCalled.Should().BeTrue();
    }

    [Test]
    public void It_does_not_set_an_error_response()
    {
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    private static ProfileWritePipelineMiddleware CreateMiddleware()
    {
        return new ProfileWritePipelineMiddleware(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );
    }

    private static ProfileContext CreateWriteProfileContext()
    {
        return new ProfileContext(
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
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Writable_Profile_Post_Create_New_With_Profile_Hiding_A_Required_Field
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo();
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
    public void It_marks_the_root_scope_as_not_creatable_on_the_wired_context()
    {
        // Load-bearing assertion for blocker 2: without effectiveSchemaRequiredMembersByScope
        // populated from RequiredFieldsForInsert, the analyzer would see zero required members
        // and RootResourceCreatable would be true even with the required field hidden.
        _requestInfo.BackendProfileWriteContext!.Request.RootResourceCreatable.Should().BeFalse();
    }

    [Test]
    public void It_still_attaches_the_backend_profile_write_context_for_executor_rejection()
    {
        // Creatability violations are deferred (deferCreatabilityViolations: true), so the
        // middleware does not short-circuit — the executor reads RootResourceCreatable and
        // produces the data-policy rejection.
        _requestInfo.BackendProfileWriteContext.Should().NotBeNull();
    }

    [Test]
    public void It_does_not_set_a_frontend_error_response_at_the_middleware_layer()
    {
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    [Test]
    public void It_calls_next_so_the_executor_can_apply_the_deferred_rejection()
    {
        _nextCalled.Should().BeTrue();
    }

    private static ProfileWritePipelineMiddleware CreateMiddleware()
    {
        return new ProfileWritePipelineMiddleware(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo()
    {
        // Build a School resource whose jsonSchemaForInsert.required includes
        // both schoolId and nameOfInstitution, so the profile below — which
        // hides nameOfInstitution via IncludeOnly: [schoolId] — triggers the
        // root-scope RootCreateRejectedWhenNonCreatable failure.
        var schoolSchema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Properties(
                ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                ("nameOfInstitution", new JsonSchemaBuilder().Type(SchemaValueType.String))
            )
            .Required("schoolId", "nameOfInstitution")
            .Build();

        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("School")
            .WithIdentityJsonPaths(["$.schoolId"])
            .WithJsonSchemaForInsert(schoolSchema)
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("SchoolId", "$.schoolId")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ProjectSchema projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "schools");
        var resourceInfo = CreateResourceInfo();

        return new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/schools",
                Body: """{"schoolId":255901}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        )
        {
            ParsedBody = JsonNode.Parse("""{"schoolId":255901}""")!,
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchema,
            ResourceInfo = resourceInfo,
            MappingSet = ExtractDocumentInfoMiddlewareTests.CreateMappingSet(resourceInfo),
            ProfileContext = CreateWriteProfileContext(),
        };
    }

    private static ResourceInfo CreateResourceInfo()
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("School"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    private static ProfileContext CreateWriteProfileContext()
    {
        // IncludeOnly with just "schoolId" leaves "nameOfInstitution" hidden,
        // even though it is required-for-insert on the resource schema.
        return new ProfileContext(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "School",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(
                    MemberSelection.IncludeOnly,
                    Properties: [new PropertyRule("schoolId")],
                    Objects: [],
                    Collections: [],
                    Extensions: []
                )
            ),
            WasExplicitlySpecified: true
        );
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Slice 5 blocker fixture: per-scope schema-required-members map
//
//  These fixtures exercise the wiring that derives required members per scope
//  from jsonSchemaForInsert (rather than only the root "$" entry). They cover
//  the three shapes the analyzer evaluates:
//
//    1. Non-root non-collection scope (inlined via the writable profile tree)
//    2. Collection item scope (inlined via the writable profile tree)
//    3. Stored-state projection rerun on POST-as-update / update flows
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_A_Writable_Profile_Post_Create_With_Profile_Hiding_A_Required_Nested_Object_Member
{
    private RequestInfo _requestInfo = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo();

        await CreateMiddleware().Execute(_requestInfo, () => Task.CompletedTask);
    }

    [Test]
    public void It_marks_the_nested_non_collection_scope_as_not_creatable()
    {
        // Without a per-scope required-members map the analyzer would see zero
        // required members at $.schoolReference and incorrectly mark it
        // Creatable=true even though the profile hides schoolYear.
        _requestInfo
            .BackendProfileWriteContext!.Request.RequestScopeStates.Should()
            .Contain(state => state.Address.JsonScope == "$.schoolReference" && !state.Creatable);
    }

    [Test]
    public void It_demotes_the_root_via_bottom_up_co_creation_propagation()
    {
        // Bottom-up propagation should mark the root non-creatable because the
        // co-created child scope is non-creatable.
        _requestInfo.BackendProfileWriteContext!.Request.RootResourceCreatable.Should().BeFalse();
    }

    [Test]
    public void It_attaches_the_backend_profile_write_context_for_executor_rejection()
    {
        // Creatability is deferred (deferCreatabilityViolations: true) so the
        // executor consumes the deferred decision rather than the middleware
        // short-circuiting with a 403.
        _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        _requestInfo.BackendProfileWriteContext.Should().NotBeNull();
    }

    private static ProfileWritePipelineMiddleware CreateMiddleware()
    {
        return new ProfileWritePipelineMiddleware(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo()
    {
        // Schema's $.schoolReference requires both schoolId and schoolYear.
        // The profile below exposes schoolReference but only includes schoolId,
        // hiding schoolYear — the new builder must surface that hidden required
        // member to the analyzer.
        var resourceSchema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(
                ("studentUniqueId", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                (
                    "schoolReference",
                    new JsonSchemaBuilder()
                        .Type(SchemaValueType.Object)
                        .Properties(
                            ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                            ("schoolYear", new JsonSchemaBuilder().Type(SchemaValueType.Integer))
                        )
                        .Required("schoolId", "schoolYear")
                )
            )
            .Required("studentUniqueId")
            .Build();

        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("StudentSchoolEnrollment")
            .WithIdentityJsonPaths(["$.studentUniqueId"])
            .WithJsonSchemaForInsert(resourceSchema)
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ProjectSchema projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        ResourceSchema resourceSchemaModel = BuildResourceSchema(
            apiSchemaDocuments,
            "studentSchoolEnrollments"
        );
        var resourceInfo = CreateResourceInfo();

        return new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/studentSchoolEnrollments",
                Body: """{"studentUniqueId":"S001","schoolReference":{"schoolId":100}}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        )
        {
            ParsedBody = JsonNode.Parse("""{"studentUniqueId":"S001","schoolReference":{"schoolId":100}}""")!,
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchemaModel,
            ResourceInfo = resourceInfo,
            MappingSet = ExtractDocumentInfoMiddlewareTests.CreateMappingSet(resourceInfo),
            ProfileContext = CreateWriteProfileContext(),
        };
    }

    private static ResourceInfo CreateResourceInfo()
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("StudentSchoolEnrollment"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    private static ProfileContext CreateWriteProfileContext()
    {
        return new ProfileContext(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "StudentSchoolEnrollment",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(
                    MemberSelection: MemberSelection.IncludeOnly,
                    Properties: [new PropertyRule("studentUniqueId")],
                    Objects:
                    [
                        new ObjectRule(
                            Name: "schoolReference",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("schoolId")],
                            NestedObjects: null,
                            Collections: null,
                            Extensions: null
                        ),
                    ],
                    Collections: [],
                    Extensions: []
                )
            ),
            WasExplicitlySpecified: true
        );
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Writable_Profile_Post_Create_With_Profile_Hiding_A_Required_Collection_Item_Member
{
    private RequestInfo _requestInfo = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo();

        await CreateMiddleware().Execute(_requestInfo, () => Task.CompletedTask);
    }

    [Test]
    public void It_marks_the_collection_item_as_not_creatable()
    {
        // Without a per-scope required-members map the analyzer would see zero
        // required members at $.classPeriods[*] and treat the new item as
        // Creatable=true even though officialAttendancePeriod is required and
        // hidden by the profile.
        _requestInfo
            .BackendProfileWriteContext!.Request.VisibleRequestCollectionItems.Should()
            .Contain(item => item.Address.JsonScope == "$.classPeriods[*]" && !item.Creatable);
    }

    [Test]
    public void It_demotes_the_root_via_bottom_up_co_creation_propagation()
    {
        _requestInfo.BackendProfileWriteContext!.Request.RootResourceCreatable.Should().BeFalse();
    }

    private static ProfileWritePipelineMiddleware CreateMiddleware()
    {
        return new ProfileWritePipelineMiddleware(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo()
    {
        var resourceSchema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(
                ("studentUniqueId", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                (
                    "classPeriods",
                    new JsonSchemaBuilder()
                        .Type(SchemaValueType.Array)
                        .Items(
                            new JsonSchemaBuilder()
                                .Type(SchemaValueType.Object)
                                .Properties(
                                    ("classPeriodName", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                                    (
                                        "officialAttendancePeriod",
                                        new JsonSchemaBuilder().Type(SchemaValueType.Boolean)
                                    )
                                )
                                .Required("classPeriodName", "officialAttendancePeriod")
                        )
                )
            )
            .Required("studentUniqueId")
            .Build();

        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("StudentSchoolEnrollment")
            .WithIdentityJsonPaths(["$.studentUniqueId"])
            .WithJsonSchemaForInsert(resourceSchema)
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ProjectSchema projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        ResourceSchema resourceSchemaModel = BuildResourceSchema(
            apiSchemaDocuments,
            "studentSchoolEnrollments"
        );
        var resourceInfo = CreateResourceInfo();

        return new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/studentSchoolEnrollments",
                Body: """{"studentUniqueId":"S001","classPeriods":[{"classPeriodName":"P1"}]}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        )
        {
            ParsedBody = JsonNode.Parse(
                """{"studentUniqueId":"S001","classPeriods":[{"classPeriodName":"P1"}]}"""
            )!,
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchemaModel,
            ResourceInfo = resourceInfo,
            MappingSet = ExtractDocumentInfoMiddlewareTests.CreateMappingSet(resourceInfo),
            ProfileContext = CreateWriteProfileContext(),
        };
    }

    private static ResourceInfo CreateResourceInfo()
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("StudentSchoolEnrollment"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    private static ProfileContext CreateWriteProfileContext()
    {
        // The collection rule includes only classPeriodName (the semantic
        // identity); officialAttendancePeriod is hidden but schema-required.
        return new ProfileContext(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "StudentSchoolEnrollment",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(
                    MemberSelection: MemberSelection.IncludeOnly,
                    Properties: [new PropertyRule("studentUniqueId")],
                    Objects: [],
                    Collections:
                    [
                        new CollectionRule(
                            Name: "classPeriods",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("classPeriodName")],
                            NestedObjects: null,
                            NestedCollections: null,
                            Extensions: null,
                            ItemFilter: null
                        ),
                    ],
                    Extensions: []
                )
            ),
            WasExplicitlySpecified: true
        );
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Writable_Profile_Post_Update_Stored_State_Projection_With_Hidden_Required_Child_Member
{
    private RequestInfo _requestInfo = null!;
    private ProfileAppliedWriteContext _storedStateContext = null!;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = CreateRequestInfo();

        await CreateMiddleware().Execute(_requestInfo, () => Task.CompletedTask);

        // Stored doc lacks schoolReference. The projection rerun should treat
        // the inbound child as a brand-new visible create — the captured map
        // must still describe the schema-required members at $.schoolReference
        // for the analyzer to flag the hidden required schoolYear.
        _storedStateContext =
            _requestInfo.BackendProfileWriteContext!.StoredStateProjectionInvoker.ProjectStoredState(
                JsonNode.Parse("""{"studentUniqueId":"S001"}""")!,
                _requestInfo.BackendProfileWriteContext.Request,
                _requestInfo.BackendProfileWriteContext.CompiledScopeCatalog
            );
    }

    [Test]
    public void It_marks_the_new_child_scope_as_not_creatable_during_projection_rerun()
    {
        // Pre-fix: the captured invoker passed an empty required-members map,
        // so the projection rerun would see no required members at
        // $.schoolReference and mark it Creatable=true even on a brand-new
        // visible create with a hidden required member.
        _storedStateContext
            .Request.RequestScopeStates.Should()
            .Contain(state => state.Address.JsonScope == "$.schoolReference" && !state.Creatable);
    }

    private static ProfileWritePipelineMiddleware CreateMiddleware()
    {
        return new ProfileWritePipelineMiddleware(
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            ),
            NullLogger<ProfileWritePipelineMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo()
    {
        var resourceSchema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(
                ("studentUniqueId", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                (
                    "schoolReference",
                    new JsonSchemaBuilder()
                        .Type(SchemaValueType.Object)
                        .Properties(
                            ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                            ("schoolYear", new JsonSchemaBuilder().Type(SchemaValueType.Integer))
                        )
                        .Required("schoolId", "schoolYear")
                )
            )
            .Required("studentUniqueId")
            .Build();

        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("StudentSchoolEnrollment")
            .WithIdentityJsonPaths(["$.studentUniqueId"])
            .WithJsonSchemaForInsert(resourceSchema)
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ProjectSchema projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        ResourceSchema resourceSchemaModel = BuildResourceSchema(
            apiSchemaDocuments,
            "studentSchoolEnrollments"
        );
        var resourceInfo = CreateResourceInfo();

        return new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/studentSchoolEnrollments",
                Body: """{"studentUniqueId":"S001","schoolReference":{"schoolId":100}}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        )
        {
            ParsedBody = JsonNode.Parse("""{"studentUniqueId":"S001","schoolReference":{"schoolId":100}}""")!,
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchemaModel,
            ResourceInfo = resourceInfo,
            MappingSet = ExtractDocumentInfoMiddlewareTests.CreateMappingSet(resourceInfo),
            ProfileContext = CreateWriteProfileContext(),
        };
    }

    private static ResourceInfo CreateResourceInfo()
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("StudentSchoolEnrollment"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    private static ProfileContext CreateWriteProfileContext()
    {
        return new ProfileContext(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: new ResourceProfile(
                ResourceName: "StudentSchoolEnrollment",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(
                    MemberSelection: MemberSelection.IncludeOnly,
                    Properties: [new PropertyRule("studentUniqueId")],
                    Objects:
                    [
                        new ObjectRule(
                            Name: "schoolReference",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("schoolId")],
                            NestedObjects: null,
                            Collections: null,
                            Extensions: null
                        ),
                    ],
                    Collections: [],
                    Extensions: []
                )
            ),
            WasExplicitlySpecified: true
        );
    }
}
