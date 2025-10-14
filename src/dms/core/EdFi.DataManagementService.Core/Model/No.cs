// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// Null objects to avoid nullable types when null is irrelevant
/// </summary>
internal static class No
{
    /// <summary>
    /// The null object for ApiSchemaDocument
    /// </summary>
    public static readonly ApiSchemaDocuments ApiSchemaDocument = new(
        new(new JsonObject(), []),
        NullLogger<ApiSchemaDocuments>.Instance
    );

    /// <summary>
    /// The null object for ProjectSchema
    /// </summary>
    public static readonly ProjectSchema ProjectSchema = new(
        new JsonObject(),
        NullLogger<ProjectSchema>.Instance
    );

    /// <summary>
    /// The null object for ResourceSchema
    /// </summary>
    public static readonly ResourceSchema ResourceSchema = new(new JsonObject());

    /// <summary>
    /// The null object for DocumentUuid
    /// </summary>
    public static readonly DocumentUuid DocumentUuid = new(Guid.Empty);

    /// <summary>
    /// The null object for ReferentialId
    /// </summary>
    public static readonly ReferentialId ReferentialId = new(Guid.Empty);

    public static readonly JsonNode JsonNode = new JsonObject();

    public static readonly PaginationParameters PaginationParameters = new(0, 0, false, 0);

    /// <summary>
    /// The null object for PathComponents
    /// </summary>
    public static readonly PathComponents PathComponents = new(
        ProjectEndpointName: new(""),
        EndpointName: new(""),
        DocumentUuid: DocumentUuid
    );

    /// <summary>
    /// The null object for ResourceInfo
    /// </summary>
    public static readonly ResourceInfo ResourceInfo = new(
        ProjectName: new ProjectName(""),
        ResourceName: new ResourceName(""),
        IsDescriptor: false,
        ResourceVersion: new SemVer(""),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, default),
        AuthorizationSecurableInfo: []
    );

    /// <summary>
    /// The null object for DocumentInfo
    /// </summary>
    public static readonly DocumentInfo DocumentInfo = new(
        DocumentIdentity: new DocumentIdentity([]),
        ReferentialId: new(Guid.Empty),
        DocumentReferences: [],
        DocumentReferenceArrays: [],
        DescriptorReferences: [],
        SuperclassIdentity: null
    );

    /// <summary>
    /// The null object for FrontendRequest
    /// </summary>
    public static FrontendRequest CreateFrontendRequest(string traceId) =>
        new(
            Body: "{}",
            Headers: [],
            Path: "",
            QueryParameters: [],
            TraceId: new TraceId(traceId),
            RouteQualifiers: []
        );

    /// <summary>
    /// The null object for FrontendResponse
    /// </summary>
    public static readonly IFrontendResponse FrontendResponse = new FrontendResponse(
        StatusCode: 503,
        Body: null,
        Headers: []
    );

    /// <summary>
    /// The null object for DocumentSecurityElements
    /// </summary>
    public static readonly DocumentSecurityElements DocumentSecurityElements = new([], [], [], [], []);

    /// <summary>
    /// The null object for ResourceClaim
    /// </summary>
    public static readonly ResourceClaim ResourceClaim = new("", "", []);

    /// <summary>
    /// The null object for EducationOrganizationHierarchyInfo
    /// </summary>
    public static readonly EducationOrganizationHierarchyInfo EducationOrganizationHierarchyInfo =
        new EducationOrganizationHierarchyInfo(
            IsInEducationOrganizationHierarchy: false,
            Id: default,
            ParentId: default
        );

    /// <summary>
    /// The null object for AuthorizationSecurableInfo
    /// </summary>
    public static readonly AuthorizationSecurableInfo[] AuthorizationSecurableInfo =
    [
        new AuthorizationSecurableInfo(""),
    ];

    /// <summary>
    /// A constructor of a RequestInfo initialized with null objects
    /// </summary>
    public static RequestInfo RequestInfo(string traceId = "")
    {
        return new RequestInfo(CreateFrontendRequest(traceId), RequestMethod.GET);
    }

    /// <summary>
    /// The null object for AuthorizationPathways
    /// </summary>
    public static readonly IReadOnlyList<AuthorizationPathway> AuthorizationPathways = [];

    /// <summary>
    /// The null object for ClientAuthorizations
    /// </summary>
    public static readonly ClientAuthorizations ClientAuthorizations = new(
        TokenId: "",
        ClientId: "",
        ClaimSetName: "",
        EducationOrganizationIds: [],
        NamespacePrefixes: []
    );
}
