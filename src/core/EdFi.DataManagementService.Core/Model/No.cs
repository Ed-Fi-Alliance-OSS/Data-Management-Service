// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// Null objects to avoid nullable types when null is irrelevant
/// </summary>
internal static class No
{
    /// <summary>
    /// The null object for ApiSchemaDocument
    /// </summary>
    public static readonly ApiSchemaDocument ApiSchemaDocument =
        new(new JsonObject(), NullLogger<ApiSchemaDocument>.Instance);

    /// <summary>
    /// The null object for ProjectSchema
    /// </summary>
    public static readonly ProjectSchema ProjectSchema =
        new(new JsonObject(), NullLogger<ProjectSchema>.Instance);

    /// <summary>
    /// The null object for ResourceSchema
    /// </summary>
    public static readonly ResourceSchema ResourceSchema =
        new(new JsonObject(), NullLogger<ResourceSchema>.Instance);

    /// <summary>
    /// The null object for DocumentUuid
    /// </summary>
    public static readonly DocumentUuid DocumentUuid = new("00000000-0000-0000-0000-000000000000");

    public static readonly JsonNode JsonNode = new JsonObject();

    /// <summary>
    /// The null object for PathComponents
    /// </summary>
    public static readonly PathComponents PathComponents =
        new(ProjectNamespace: new(""), EndpointName: new(""), DocumentUuid: DocumentUuid);

    /// <summary>
    /// The null object for ResourceInfo
    /// </summary>
    public static readonly ResourceInfo ResourceInfo =
        new(
            ProjectName: new MetaEdProjectName(""),
            ResourceName: new MetaEdResourceName(""),
            IsDescriptor: false,
            ResourceVersion: new SemVer(""),
            AllowIdentityUpdates: false
        );

    /// <summary>
    /// The null object for DocumentInfo
    /// </summary>
    public static readonly DocumentInfo DocumentInfo =
        new(
            DocumentIdentity: new DocumentIdentity([]),
            DocumentReferences: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

    /// <summary>
    /// The null object for FrontendRequest
    /// </summary>
    public static readonly FrontendRequest FrontendRequest =
        new(Body: "{}", Path: "", QueryParameters: [], TraceId: new TraceId(""));

    /// <summary>
    /// The null object for FrontendResponse
    /// </summary>
    public static readonly FrontendResponse FrontendResponse = new(StatusCode: 503, Body: "", Headers: []);

    /// <summary>
    /// A constructor of a PipelineContext initialized with null objects
    /// </summary>
    public static PipelineContext PipelineContext()
    {
        return new PipelineContext(FrontendRequest, RequestMethod.GET);
    }
}
