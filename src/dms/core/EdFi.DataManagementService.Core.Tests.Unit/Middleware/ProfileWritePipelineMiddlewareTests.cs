// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
