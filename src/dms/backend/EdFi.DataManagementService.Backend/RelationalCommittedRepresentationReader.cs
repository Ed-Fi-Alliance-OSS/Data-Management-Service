// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

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
    ISessionDocumentHydrator sessionDocumentHydrator,
    IRelationalReadMaterializer readMaterializer
) : IRelationalCommittedRepresentationReader
{
    private readonly ISessionDocumentHydrator _sessionDocumentHydrator =
        sessionDocumentHydrator ?? throw new ArgumentNullException(nameof(sessionDocumentHydrator));
    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));

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

        var readPlan =
            request.ExistingDocumentReadPlan
            ?? throw new InvalidOperationException(
                RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(
                    request.WritePlan.Model.Resource
                )
            );

        var hydratedPage = await _sessionDocumentHydrator
            .HydrateAsync(
                writeSession.Connection,
                writeSession.Transaction,
                readPlan,
                new PageKeysetSpec.Single(persistedTarget.DocumentId),
                new HydrationExecutionOptions(IncludeDescriptorProjection: true),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (hydratedPage.DocumentMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"Committed relational write readback for document id {persistedTarget.DocumentId} returned "
                    + $"{hydratedPage.DocumentMetadata.Count} metadata rows, but exactly 1 was expected."
            );
        }

        var documentMetadata = hydratedPage.DocumentMetadata[0];

        if (
            documentMetadata.DocumentId != persistedTarget.DocumentId
            || documentMetadata.DocumentUuid != persistedTarget.DocumentUuid.Value
        )
        {
            throw new InvalidOperationException(
                $"Committed relational write readback returned metadata for document id {documentMetadata.DocumentId} / "
                    + $"uuid '{documentMetadata.DocumentUuid}', but persistence committed "
                    + $"id {persistedTarget.DocumentId} / uuid '{persistedTarget.DocumentUuid.Value}'."
            );
        }

        return _readMaterializer.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                documentMetadata,
                hydratedPage.TableRowsInDependencyOrder,
                hydratedPage.DescriptorRowsInPlanOrder,
                RelationalGetRequestReadMode.ExternalResponse
            )
        );
    }
}
