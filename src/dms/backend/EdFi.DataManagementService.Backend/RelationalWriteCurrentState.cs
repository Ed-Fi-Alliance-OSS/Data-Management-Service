// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed record RelationalWriteCurrentStateLoadRequest
{
    public RelationalWriteCurrentStateLoadRequest(
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        bool requiresReconstitution = false
    )
    {
        ReadPlan = readPlan ?? throw new ArgumentNullException(nameof(readPlan));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        RequiresReconstitution = requiresReconstitution;
    }

    public ResourceReadPlan ReadPlan { get; init; }

    public RelationalWriteTargetContext.ExistingDocument TargetContext { get; init; }

    /// <summary>
    /// When <c>true</c>, the loader reconstitutes the stored JSON document so the result includes
    /// <see cref="RelationalWriteCurrentState.ReconstitutedDocument"/> for profile-constrained flows.
    /// When <c>false</c>, the extra descriptor hydration and in-memory assembly are skipped.
    /// </summary>
    public bool RequiresReconstitution { get; init; }
}

internal sealed record RelationalWriteCurrentState
{
    public RelationalWriteCurrentState(
        DocumentMetadataRow documentMetadata,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder
    )
    {
        DocumentMetadata = documentMetadata ?? throw new ArgumentNullException(nameof(documentMetadata));
        TableRowsInDependencyOrder =
            tableRowsInDependencyOrder ?? throw new ArgumentNullException(nameof(tableRowsInDependencyOrder));
    }

    public DocumentMetadataRow DocumentMetadata { get; init; }

    public IReadOnlyList<HydratedTableRows> TableRowsInDependencyOrder { get; init; }

    /// <summary>
    /// The fully reconstituted stored JSON document, including descriptor URIs and relational overlays,
    /// when requested by the caller.
    /// </summary>
    public JsonNode? ReconstitutedDocument { get; init; }
}

internal interface IRelationalWriteCurrentStateLoader
{
    Task<RelationalWriteCurrentState?> LoadAsync(
        RelationalWriteCurrentStateLoadRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal interface ISessionDocumentHydrator
{
    Task<HydratedPage> HydrateAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteCurrentStateLoader : IRelationalWriteCurrentStateLoader
{
    private readonly ISessionDocumentHydrator _sessionDocumentHydrator;

    public RelationalWriteCurrentStateLoader(ISessionDocumentHydrator sessionDocumentHydrator)
    {
        _sessionDocumentHydrator =
            sessionDocumentHydrator ?? throw new ArgumentNullException(nameof(sessionDocumentHydrator));
    }

    public async Task<RelationalWriteCurrentState?> LoadAsync(
        RelationalWriteCurrentStateLoadRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeSession);

        var hydratedPage = await _sessionDocumentHydrator
            .HydrateAsync(
                writeSession.Connection,
                writeSession.Transaction,
                request.ReadPlan,
                new PageKeysetSpec.Single(request.TargetContext.DocumentId),
                new HydrationExecutionOptions(IncludeDescriptorProjection: request.RequiresReconstitution),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (hydratedPage.DocumentMetadata.Count != 1)
        {
            if (hydratedPage.DocumentMetadata.Count == 0)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"Current-state load for document id {request.TargetContext.DocumentId} returned "
                    + $"{hydratedPage.DocumentMetadata.Count} metadata rows, but exactly 1 was expected."
            );
        }

        var documentMetadata = hydratedPage.DocumentMetadata[0];

        if (documentMetadata.DocumentId != request.TargetContext.DocumentId)
        {
            throw new InvalidOperationException(
                $"Current-state load returned metadata for document id {documentMetadata.DocumentId}, "
                    + $"but target document id was {request.TargetContext.DocumentId}."
            );
        }

        if (!request.RequiresReconstitution)
        {
            return new RelationalWriteCurrentState(documentMetadata, hydratedPage.TableRowsInDependencyOrder);
        }

        Dictionary<long, string> descriptorUriLookup = [];

        foreach (var descriptorRows in hydratedPage.DescriptorRowsInPlanOrder)
        {
            foreach (var row in descriptorRows.Rows)
            {
                descriptorUriLookup.TryAdd(row.DescriptorId, row.Uri);
            }
        }

        var reconstitutedDocument = DocumentReconstituter.Reconstitute(
            documentMetadata.DocumentId,
            hydratedPage.TableRowsInDependencyOrder,
            request.ReadPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            request.ReadPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
        );

        return new RelationalWriteCurrentState(documentMetadata, hydratedPage.TableRowsInDependencyOrder)
        {
            ReconstitutedDocument = reconstitutedDocument,
        };
    }
}
