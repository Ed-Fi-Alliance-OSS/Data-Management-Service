// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// A bounded, safe-to-display rejection from the SQL Server physical-schema preflight.
/// </summary>
internal sealed class MssqlPhysicalSchemaCompatibilityException(SqlException exception)
    : InvalidOperationException(GuardMessage(exception.Number), exception)
{
    internal static bool IsGuardFailure(SqlException exception) => GuardMessage(exception.Number) is not null;

    private static string? GuardMessage(int number) =>
        number switch
        {
            MssqlDocumentCachePhysicalSchema.UnsupportedServerError =>
                MssqlDocumentCachePhysicalSchema.UnsupportedServerMessage,
            MssqlDocumentCachePhysicalSchema.LegacySchemaError =>
                MssqlDocumentCachePhysicalSchema.LegacySchemaMessage,
            MssqlDocumentCachePhysicalSchema.IncompatibleSchemaError =>
                MssqlDocumentCachePhysicalSchema.IncompatibleSchemaMessage,
            _ => null,
        };
}
