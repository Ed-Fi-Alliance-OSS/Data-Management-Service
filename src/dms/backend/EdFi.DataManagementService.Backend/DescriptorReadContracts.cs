// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Read request context for descriptor GET-by-id operations served from the shared
/// <c>dms.Descriptor</c> table.
/// </summary>
public sealed record DescriptorGetByIdRequest
{
    public DescriptorGetByIdRequest(
        MappingSet mappingSet,
        ConcreteResourceModel descriptorResourceModel,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        RelationalGetRequestReadMode readMode,
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        TraceId traceId
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        DescriptorResourceModel =
            descriptorResourceModel ?? throw new ArgumentNullException(nameof(descriptorResourceModel));
        Resource = resource;
        DocumentUuid = documentUuid;
        ReadMode = readMode;
        AuthorizationStrategyEvaluators =
            authorizationStrategyEvaluators
            ?? throw new ArgumentNullException(nameof(authorizationStrategyEvaluators));
        ReadableProfileProjectionContext = readableProfileProjectionContext;
        TraceId = traceId;
    }

    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// </summary>
    public MappingSet MappingSet { get; init; }

    /// <summary>
    /// The descriptor resource model selected from the active mapping set.
    /// </summary>
    public ConcreteResourceModel DescriptorResourceModel { get; init; }

    /// <summary>
    /// The qualified descriptor resource being retrieved.
    /// </summary>
    public QualifiedResourceName Resource { get; init; }

    /// <summary>
    /// The external document UUID supplied on the GET-by-id request.
    /// </summary>
    public DocumentUuid DocumentUuid { get; init; }

    /// <summary>
    /// Controls whether the response should be materialized as an external response or stored document.
    /// </summary>
    public RelationalGetRequestReadMode ReadMode { get; init; }

    /// <summary>
    /// The effective GET authorization strategies already resolved by Core.
    /// </summary>
    public AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; init; }

    /// <summary>
    /// Optional readable-profile projection inputs for external-response reads.
    /// </summary>
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }
}

/// <summary>
/// Read request context for descriptor GET-many/query operations served from the shared
/// <c>dms.Descriptor</c> table.
/// </summary>
public sealed record DescriptorQueryRequest
{
    public DescriptorQueryRequest(
        MappingSet mappingSet,
        ConcreteResourceModel descriptorResourceModel,
        QualifiedResourceName resource,
        QueryElement[] queryElements,
        PaginationParameters paginationParameters,
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        TraceId traceId
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        DescriptorResourceModel =
            descriptorResourceModel ?? throw new ArgumentNullException(nameof(descriptorResourceModel));
        Resource = resource;
        QueryElements = queryElements ?? throw new ArgumentNullException(nameof(queryElements));
        PaginationParameters =
            paginationParameters ?? throw new ArgumentNullException(nameof(paginationParameters));
        AuthorizationStrategyEvaluators =
            authorizationStrategyEvaluators
            ?? throw new ArgumentNullException(nameof(authorizationStrategyEvaluators));
        ReadableProfileProjectionContext = readableProfileProjectionContext;
        TraceId = traceId;
    }

    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// </summary>
    public MappingSet MappingSet { get; init; }

    /// <summary>
    /// The descriptor resource model selected from the active mapping set.
    /// </summary>
    public ConcreteResourceModel DescriptorResourceModel { get; init; }

    /// <summary>
    /// The qualified descriptor resource being queried.
    /// </summary>
    public QualifiedResourceName Resource { get; init; }

    /// <summary>
    /// The client query elements after Core validation and parsing.
    /// </summary>
    public QueryElement[] QueryElements { get; init; }

    /// <summary>
    /// The paging inputs for descriptor GET-many execution.
    /// </summary>
    public PaginationParameters PaginationParameters { get; init; }

    /// <summary>
    /// The effective GET-many authorization strategies already resolved by Core.
    /// </summary>
    public AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; init; }

    /// <summary>
    /// Optional readable-profile projection inputs for external-response reads.
    /// </summary>
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }
}

/// <summary>
/// Handles descriptor resource reads from the shared <c>dms.Descriptor</c> table,
/// bypassing the generic project-schema read path.
/// </summary>
public interface IDescriptorReadHandler
{
    /// <summary>
    /// Executes a descriptor GET-by-id read.
    /// </summary>
    Task<GetResult> HandleGetByIdAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Executes a descriptor GET-many/query read.
    /// </summary>
    Task<QueryResult> HandleQueryAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken = default
    );
}
