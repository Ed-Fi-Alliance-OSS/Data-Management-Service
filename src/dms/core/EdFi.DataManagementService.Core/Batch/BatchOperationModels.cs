// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Batch;

internal enum BatchOperationType
{
    Create,
    Update,
    Delete,
}

internal static class BatchOperationTypeExtensions
{
    public static string ToOperationString(this BatchOperationType operationType) =>
        operationType switch
        {
            BatchOperationType.Create => "create",
            BatchOperationType.Update => "update",
            BatchOperationType.Delete => "delete",
            _ => throw new ArgumentOutOfRangeException(nameof(operationType), operationType, null),
        };

    public static bool TryParse(string? value, out BatchOperationType operationType)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "create":
                operationType = BatchOperationType.Create;
                return true;
            case "update":
                operationType = BatchOperationType.Update;
                return true;
            case "delete":
                operationType = BatchOperationType.Delete;
                return true;
            default:
                operationType = BatchOperationType.Create;
                return false;
        }
    }
}

internal sealed record BatchOperation(
    int Index,
    BatchOperationType OperationType,
    ResourceName Resource,
    JsonObject? Document,
    JsonObject? NaturalKey,
    DocumentUuid? DocumentId,
    string? IfMatch
);

internal sealed record BatchOperationSuccess(
    int Index,
    BatchOperationType OperationType,
    ResourceName Resource,
    DocumentUuid DocumentUuid
);

internal sealed record BatchOperationFailure(BatchOperation Operation, FrontendResponse ErrorResponse);
