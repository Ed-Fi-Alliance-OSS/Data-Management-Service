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
