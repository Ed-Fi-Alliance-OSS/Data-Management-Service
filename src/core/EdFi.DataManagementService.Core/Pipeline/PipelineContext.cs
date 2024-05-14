// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// The full context of the request, enriched by pipeline steps and the final handler
/// </summary>
public class PipelineContext(FrontendRequest _frontendRequest, RequestMethod _method)
{
    /// <summary>
    /// An API request sent from the frontend to be processed
    /// </summary>
    public FrontendRequest FrontendRequest
    {
        get => _frontendRequest;
        set => _frontendRequest = value;
    }

    /// <summary>
    /// The request method from a DMS frontend - GET, POST, PUT, DELETE
    /// </summary>
    public RequestMethod Method
    {
        get => _method;
        set => _method = value;
    }

    /// <summary>
    /// The important parts of the request URL path in object form
    /// </summary>
    public PathComponents PathComponents { get; set; } = No.PathComponents;

    /// <summary>
    /// Provides information from an ApiSchema.json document
    /// </summary>
    public ApiSchemaDocument ApiSchemaDocument { get; set; } = No.ApiSchemaDocument;

    /// <summary>
    /// Provides information from the ProjectSchema portion of an ApiSchema.json document
    /// </summary>
    public ProjectSchema ProjectSchema { get; set; } = No.ProjectSchema;

    /// <summary>
    /// Provides information from the ResourceSchema portion of an ApiSchema.json document
    /// </summary>
    public ResourceSchema ResourceSchema { get; set; } = No.ResourceSchema;

    /// <summary>
    /// API resource information for passing along to backends.
    /// </summary>
    public ResourceInfo ResourceInfo { get; set; } = No.ResourceInfo;

    /// <summary>
    /// API document information for passing along to backends.
    /// </summary>
    public DocumentInfo DocumentInfo { get; set; } = No.DocumentInfo;

    /// <summary>
    /// The API response to be returned to the frontend
    /// </summary>
    public FrontendResponse FrontendResponse { get; set; } = No.FrontendResponse;
}
