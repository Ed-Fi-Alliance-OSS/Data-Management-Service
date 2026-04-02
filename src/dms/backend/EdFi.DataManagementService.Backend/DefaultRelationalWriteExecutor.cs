// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal sealed class DefaultRelationalWriteExecutor : IRelationalWriteExecutor
{
    public Task<RelationalWriteExecutorResult> ExecuteAsync(
        RelationalWriteExecutorRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resource = request.WritePlan.Model.Resource;
        var failureMessage = RelationalWriteSupport.BuildWriteExecutionNotImplementedMessage(
            request.OperationKind,
            resource
        );

        var result = request.OperationKind switch
        {
            RelationalWriteOperationKind.Post => (RelationalWriteExecutorResult)
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UnknownFailure(failureMessage)),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(failureMessage)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
        };

        return Task.FromResult(result);
    }
}
