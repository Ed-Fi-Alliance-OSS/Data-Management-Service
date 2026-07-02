// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalCommittedRepresentationReader(
    IEtagComposer etagComposer,
    IOptions<ResourceLinksOptions> linksOptions
) : IRelationalCommittedRepresentationReader
{
    private const string EtagPropertyName = "_etag";

    private readonly IEtagComposer _etagComposer =
        etagComposer ?? throw new ArgumentNullException(nameof(etagComposer));
    private readonly ResourceLinksOptions _linksOptions =
        linksOptions?.Value ?? throw new ArgumentNullException(nameof(linksOptions));

    public async Task<JsonNode> ReadAsync(
        RelationalWriteExecutorRequest request,
        RelationalWritePersistResult persistedTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(persistedTarget);
        ArgumentNullException.ThrowIfNull(writeSession);

        // The write response carries only the _etag header. Read just the stamped ContentVersion for
        // the persisted document (the content-stamp trigger bumps dms.Document.ContentVersion within
        // this transaction) and compose the etag — no hydrate, no materialization, no hashing.
        await using var command = writeSession.CreateCommand(
            RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(
                request.MappingSet.Key.Dialect,
                persistedTarget.DocumentId
            )
        );

        var scalarResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalarResult is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Committed relational write readback found no ContentVersion for document id "
                    + $"{persistedTarget.DocumentId}."
            );
        }

        var contentVersion = Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture);
        var variantKey = VariantKeyFactory.Create(
            request.MappingSet.Key.EffectiveSchemaHash,
            ResponseFormat.Json,
            ProfileVariantCode.Of(request.ProfileWriteContext?.ProfileName),
            _linksOptions.Enabled
        );

        return new JsonObject { [EtagPropertyName] = _etagComposer.Compose(contentVersion, variantKey) };
    }
}
