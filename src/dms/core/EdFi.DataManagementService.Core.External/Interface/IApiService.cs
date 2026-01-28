// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// The facade a frontend uses to access DMS Core API services.
///
/// The intent of this design is to provide a web framework-independent interface
/// for 1) ease of testing and 2) ease of supporting future front ends e.g.
/// AWS Lambda, Azure functions, etc.
/// </summary>
public interface IApiService
{
    /// <summary>
    /// DMS entry point for API upsert requests
    /// </summary>
    Task<IFrontendResponse> Upsert(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for all API GET by id requests
    /// </summary>
    Task<IFrontendResponse> Get(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for all API PUT requests, which are "by id"
    /// </summary>
    Task<IFrontendResponse> UpdateById(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for all API DELETE requests, which are "by id"
    /// </summary>
    Task<IFrontendResponse> DeleteById(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for the token introspection request
    /// </summary>
    Task<IFrontendResponse> GetTokenInfo(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for data model information from ApiSchema.json
    /// </summary>
    IList<IDataModelInfo> GetDataModelInfo();

    /// <summary>
    /// DMS entry point to get resource dependencies
    /// </summary>
    /// <returns>JSON array ordered by dependency sequence</returns>
    JsonArray GetDependencies();

    /// <summary>
    /// Retrieves dependency data in the GraphML format.
    /// </summary>
    /// <returns>
    /// A GraphML model representing dependencies to be serialized to GraphML format (XML-based).
    /// </returns>
    GraphML GetDependenciesAsGraphML();

    /// <summary>
    /// DMS entry point to get the OpenAPI specification for resources, derived from core and extension ApiSchemas
    /// Servers array should be provided by the front end.
    /// </summary>
    JsonNode GetResourceOpenApiSpecification(JsonArray servers);

    /// <summary>
    /// DMS entry point to get the OpenAPI specification for descriptors, derived from core and extension ApiSchemas
    /// Servers array should be provided by the front end.
    /// </summary>
    JsonNode GetDescriptorOpenApiSpecification(JsonArray servers);

    /// <summary>
    /// DMS entry point to reload the claimsets cache
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant deployments</param>
    Task<IFrontendResponse> ReloadClaimsetsAsync(string? tenant = null);

    /// <summary>
    /// DMS entry point to view current claimsets from the provider
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant deployments</param>
    Task<IFrontendResponse> ViewClaimsetsAsync(string? tenant = null);

    /// <summary>
    /// Gets all available profile names for a tenant (cached).
    /// </summary>
    /// <param name="tenantId">Optional tenant identifier for multi-tenant deployments</param>
    /// <returns>List of profile names</returns>
    Task<IReadOnlyList<string>> GetProfileNamesAsync(string? tenantId);

    /// <summary>
    /// Gets the OpenAPI specification for a specific profile (cached).
    /// </summary>
    /// <param name="profileName">The name of the profile</param>
    /// <param name="tenantId">Optional tenant identifier for multi-tenant deployments</param>
    /// <param name="servers">The servers array for the OpenAPI spec</param>
    /// <returns>The filtered OpenAPI specification, or null if profile not found</returns>
    Task<JsonNode?> GetProfileOpenApiSpecificationAsync(
        string profileName,
        string? tenantId,
        JsonArray servers
    );
}
