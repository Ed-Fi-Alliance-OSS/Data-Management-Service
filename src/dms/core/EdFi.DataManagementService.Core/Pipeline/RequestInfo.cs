// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// Data container for API request processing, enriched by pipeline steps and handlers
/// </summary>
internal class RequestInfo(
    FrontendRequest _frontendRequest,
    RequestMethod _method,
    IServiceProvider _scopedServiceProvider
)
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
    /// Provides information from a set of ApiSchema.json documents
    /// </summary>
    public ApiSchemaDocuments ApiSchemaDocuments { get; set; } = No.ApiSchemaDocument;

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
    public IFrontendResponse FrontendResponse { get; set; } = No.FrontendResponse;

    /// <summary>
    /// Body in Json format
    /// </summary>
    public JsonNode ParsedBody { get; set; } = No.JsonNode;

    /// <summary>
    /// Pagination parameters for GET by query
    /// </summary>
    public PaginationParameters PaginationParameters { get; set; } = No.PaginationParameters;

    /// <summary>
    /// Query elements for GET by query
    /// </summary>
    public QueryElement[] QueryElements { get; set; } = [];

    /// <summary>
    /// Collection of authorization strategy filters, each specifying
    /// collection of filters and filter operator
    /// </summary>
    public AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; set; } = [];

    /// <summary>
    /// DocumentSecurityElements from the submitted document
    /// </summary>
    public DocumentSecurityElements DocumentSecurityElements { get; set; } = No.DocumentSecurityElements;

    /// <summary>
    /// ResourceActionAuthStrategies for the request
    /// </summary>
    public IReadOnlyList<string> ResourceActionAuthStrategies { get; set; } = [];

    /// <summary>
    /// EducationOrganizationHierarchyInfo for the submitted document
    /// </summary>
    public EducationOrganizationHierarchyInfo EducationOrganizationHierarchyInfo { get; set; } =
        No.EducationOrganizationHierarchyInfo;

    /// <summary>
    /// The AuthorizationPathways the resource is part of.
    /// </summary>
    public IReadOnlyList<AuthorizationPathway> AuthorizationPathways { get; set; } = No.AuthorizationPathways;

    /// <summary>
    /// Student Authorization Securable info for the submitted document
    /// </summary>
    public AuthorizationSecurableInfo[] AuthorizationSecurableInfo { get; set; } =
        No.AuthorizationSecurableInfo;

    /// <summary>
    /// ApiDetails retrieved from the token, used for resource authorization.
    /// This will be null when the frontend passes the request, and will be populated
    /// by the JWT authentication middleware in Core.
    /// </summary>
    public ClientAuthorizations ClientAuthorizations { get; set; } = No.ClientAuthorizations;

    /// <summary>
    /// Route qualifiers extracted from the URL path (e.g., district ID, school year)
    /// that determine which DMS instance to route the request to.
    /// Empty if no route qualifiers are configured.
    /// </summary>
    public Dictionary<RouteQualifierName, RouteQualifierValue> RouteQualifiers { get; set; } = [];

    /// <summary>
    /// Profile context for profile-based data filtering, if a profile applies to this request.
    /// Null if no profile applies (no profile assigned, or not a profiled endpoint).
    /// </summary>
    public ProfileContext? ProfileContext { get; set; }

    /// <summary>
    /// The cached database fingerprint from the dms.EffectiveSchema singleton row.
    /// Set by ValidateDatabaseFingerprintMiddleware when UseRelationalBackend is true.
    /// Null when relational backend is disabled or the request short-circuits before
    /// fingerprint validation completes.
    /// </summary>
    public DatabaseFingerprint? DatabaseFingerprint { get; set; }

    /// <summary>
    /// The compiled mapping set for the current request's database instance.
    /// Set by ResolveMappingSetMiddleware when UseRelationalBackend is true.
    /// Supported relational handler paths should have this populated before repository
    /// execution. Null when relational backend is disabled or the request short-circuits
    /// before mapping-set resolution completes.
    /// </summary>
    public MappingSet? MappingSet { get; set; }

    /// <summary>
    /// Optional profile write context when a writable profile applies to the current
    /// write request. Produced by ProfileWritePipelineMiddleware. Null when no writable
    /// profile applies or the request is not a write operation.
    /// </summary>
    public BackendProfileWriteContext? BackendProfileWriteContext { get; set; }

    /// <summary>
    /// The service provider for the current request scope.
    /// Used by middlewares and handlers to resolve scoped services.
    /// </summary>
    public IServiceProvider ScopedServiceProvider
    {
        get => _scopedServiceProvider;
        set => _scopedServiceProvider = value;
    }
}
