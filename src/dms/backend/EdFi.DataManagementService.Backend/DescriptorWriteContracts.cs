// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Write request context for descriptor resources stored in the shared <c>dms.Descriptor</c> table.
/// </summary>
public sealed record DescriptorWriteRequest
{
    public DescriptorWriteRequest(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        JsonNode requestBody,
        DocumentUuid documentUuid,
        ReferentialId? referentialId,
        TraceId traceId,
        string? ifMatchEtag = null,
        BackendProfileWriteContext? backendProfileWriteContext = null
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        Resource = resource;
        RequestBody = requestBody ?? throw new ArgumentNullException(nameof(requestBody));
        DocumentUuid = documentUuid;
        ReferentialId = referentialId;
        TraceId = traceId;
        IfMatchEtag = ifMatchEtag;
        BackendProfileWriteContext = backendProfileWriteContext;
    }

    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// </summary>
    public MappingSet MappingSet { get; init; }

    /// <summary>
    /// The qualified resource name of the descriptor being written.
    /// </summary>
    public QualifiedResourceName Resource { get; init; }

    /// <summary>
    /// The request body containing descriptor fields.
    /// </summary>
    public JsonNode RequestBody { get; init; }

    /// <summary>
    /// The document UUID: a candidate for POST inserts or the existing UUID for PUT.
    /// </summary>
    public DocumentUuid DocumentUuid { get; init; }

    /// <summary>
    /// The referential id for POST target context resolution. <c>null</c> for PUT requests.
    /// </summary>
    public ReferentialId? ReferentialId { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }

    /// <summary>
    /// The client-supplied <c>If-Match</c> header value, or <c>null</c> when the header was absent.
    /// When present and non-null the handler compares this against the computed <c>_etag</c> of the
    /// current persisted descriptor representation before proceeding with the write.
    /// </summary>
    public string? IfMatchEtag { get; init; }

    public BackendProfileWriteContext? BackendProfileWriteContext { get; init; }
}

/// <summary>
/// Handles descriptor resource writes to the shared <c>dms.Descriptor</c> table,
/// bypassing the generic flatten-to-project-schema write executor.
/// </summary>
public interface IDescriptorWriteHandler
{
    /// <summary>
    /// Executes a descriptor POST (upsert) write.
    /// </summary>
    Task<UpsertResult> HandlePostAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Executes a descriptor PUT (update-by-id) write.
    /// </summary>
    Task<UpdateResult> HandlePutAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Executes a descriptor DELETE scoped to the descriptor resource identified by
    /// <paramref name="resource"/>. Scoping by <c>ResourceKeyId</c> prevents a UUID from
    /// one descriptor resource from being used to delete a different descriptor type or a
    /// non-descriptor document.
    /// </summary>
    Task<DeleteResult> HandleDeleteAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        TraceId traceId,
        string? ifMatchEtag = null,
        BackendProfileWriteContext? backendProfileWriteContext = null,
        CancellationToken cancellationToken = default
    );
}
