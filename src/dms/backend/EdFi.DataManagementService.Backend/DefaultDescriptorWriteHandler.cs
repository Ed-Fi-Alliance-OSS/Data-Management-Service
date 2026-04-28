// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Default stub descriptor write handler that returns <c>UnknownFailure</c>
/// until the descriptor write executor is implemented.
/// </summary>
internal sealed class DefaultDescriptorWriteHandler : IDescriptorWriteHandler
{
    public Task<UpsertResult> HandlePostAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<UpsertResult>(
            new UpsertResult.UnknownFailure(BuildNotImplementedMessage("POST", request))
        );
    }

    public Task<UpdateResult> HandlePutAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<UpdateResult>(
            new UpdateResult.UnknownFailure(BuildNotImplementedMessage("PUT", request))
        );
    }

    public Task<DeleteResult> HandleDeleteAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        TraceId traceId,
        string? ifMatchEtag = null,
        BackendProfileWriteContext? backendProfileWriteContext = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<DeleteResult>(
            new DeleteResult.UnknownFailure("Descriptor DELETE write is not implemented.")
        );
    }

    private static string BuildNotImplementedMessage(string operation, DescriptorWriteRequest request) =>
        $"Descriptor {operation} write is not implemented for resource "
        + $"'{RelationalWriteSupport.FormatResource(request.Resource)}'.";
}
