// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public sealed record MssqlDatabaseFileMetadata(
    int FileId,
    string LogicalName,
    string Type,
    string PhysicalName
)
{
    public string BuildRestorePath(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        var extension = Path.GetExtension(PhysicalName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Type.Equals("L", StringComparison.OrdinalIgnoreCase) ? ".ldf" : ".mdf";
        }

        var fileName =
            $"{databaseName}_{FileId}_{MssqlTestDatabaseHelper.SanitizeFileNamePart(LogicalName)}{extension}";

        return MssqlTestDatabaseHelper.BuildSiblingFilePath(PhysicalName, fileName);
    }

    public static string BuildBackupPath(string databaseName, string sourceDataFilePhysicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        return MssqlTestDatabaseHelper.BuildSiblingFilePath(
            sourceDataFilePhysicalName,
            $"{databaseName}_baseline.bak"
        );
    }
}
