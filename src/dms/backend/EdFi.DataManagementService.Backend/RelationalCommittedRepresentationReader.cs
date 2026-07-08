// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

internal interface IRelationalCommittedRepresentationReader
{
    Task<JsonNode> ReadAsync(
        RelationalWriteExecutorRequest request,
        RelationalWritePersistResult persistedTarget,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalCommittedRepresentationReader(
    IServedEtagComposer servedEtagComposer,
    IOptions<ResourceLinksOptions> linksOptions
) : IRelationalCommittedRepresentationReader
{
    private const string EtagPropertyName = "_etag";

    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));
    private readonly ResourceLinksOptions _linksOptions =
        linksOptions?.Value ?? throw new ArgumentNullException(nameof(linksOptions));

    // The write response carries only the _etag header. The final committed ContentVersion is
    // persistence metadata supplied by the persister (RelationalWritePersistResult.ContentVersion),
    // so this reader only composes the etag — no dms.Document query, no hydrate, no hashing.
    public Task<JsonNode> ReadAsync(
        RelationalWriteExecutorRequest request,
        RelationalWritePersistResult persistedTarget,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(persistedTarget);
        cancellationToken.ThrowIfCancellationRequested();

        var etag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                request.ProfileWriteContext?.ProfileName,
                _linksOptions.Enabled,
                persistedTarget.ContentVersion
            )
        );

        return Task.FromResult<JsonNode>(new JsonObject { [EtagPropertyName] = etag });
    }
}
