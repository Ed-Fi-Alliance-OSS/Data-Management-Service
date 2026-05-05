// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Descriptor read handler stub. Subsequent descriptor-read tasks will replace the
/// temporary not-implemented results with direct <c>dms.Document</c>/<c>dms.Descriptor</c> reads.
/// </summary>
internal sealed class DescriptorReadHandler(ILogger<DescriptorReadHandler> logger) : IDescriptorReadHandler
{
    private readonly ILogger<DescriptorReadHandler> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<GetResult> HandleGetByIdAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Descriptor GET-by-id routed to descriptor read handler for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        return Task.FromResult<GetResult>(
            new GetResult.GetFailureNotImplemented(
                $"Relational descriptor GET by id is not implemented for resource '{RelationalWriteSupport.FormatResource(request.Resource)}'."
            )
        );
    }

    public Task<QueryResult> HandleQueryAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Descriptor query routed to descriptor read handler for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        return Task.FromResult<QueryResult>(
            new QueryResult.QueryFailureNotImplemented(
                $"Relational descriptor GET-many is not implemented for resource '{RelationalWriteSupport.FormatResource(request.Resource)}'."
            )
        );
    }
}
