// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed record RelationalWriteCurrentStateLoadRequest
{
    public RelationalWriteCurrentStateLoadRequest(
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        bool includeDescriptorProjection = false
    )
    {
        ReadPlan = readPlan ?? throw new ArgumentNullException(nameof(readPlan));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        IncludeDescriptorProjection = includeDescriptorProjection;
    }

    public ResourceReadPlan ReadPlan { get; init; }

    public RelationalWriteTargetContext.ExistingDocument TargetContext { get; init; }

    public bool IncludeDescriptorProjection { get; init; }
}

internal sealed record RelationalWriteCurrentState
{
    public RelationalWriteCurrentState(
        DocumentMetadataRow documentMetadata,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<HydratedDescriptorRows> descriptorRowsInPlanOrder
    )
    {
        DocumentMetadata = documentMetadata ?? throw new ArgumentNullException(nameof(documentMetadata));
        TableRowsInDependencyOrder =
            tableRowsInDependencyOrder ?? throw new ArgumentNullException(nameof(tableRowsInDependencyOrder));
        DescriptorRowsInPlanOrder =
            descriptorRowsInPlanOrder ?? throw new ArgumentNullException(nameof(descriptorRowsInPlanOrder));
    }

    public DocumentMetadataRow DocumentMetadata { get; init; }

    public IReadOnlyList<HydratedTableRows> TableRowsInDependencyOrder { get; init; }

    public IReadOnlyList<HydratedDescriptorRows> DescriptorRowsInPlanOrder { get; init; }
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
                new HydrationExecutionOptions(
                    IncludeDescriptorProjection: request.IncludeDescriptorProjection
                ),
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

        return new RelationalWriteCurrentState(
            documentMetadata,
            hydratedPage.TableRowsInDependencyOrder,
            hydratedPage.DescriptorRowsInPlanOrder
        );
    }
}
