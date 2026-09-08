// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

internal static class MssqlDocumentCacheWriterDeleteRaceClassifier
{
    private const int ThrowStatementNumber = 50000;

    private static readonly MssqlRelationalWriteExceptionClassifier WriteExceptionClassifier = new();

    public static bool IsRetryableDeleteRace(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return WriteExceptionClassifier.IsForeignKeyViolation(exception)
            || IsDocumentCacheUuidTriggerFailure(exception);
    }

    private static bool IsDocumentCacheUuidTriggerFailure(SqlException exception) =>
        exception.Number == ThrowStatementNumber
        && exception.Message.StartsWith(
            DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuidFailureMessagePrefix,
            StringComparison.Ordinal
        );
}
