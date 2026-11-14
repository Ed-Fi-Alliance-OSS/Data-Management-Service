// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The engine-neutral split of the delivered inventory queries' pipe-separated rows. Both engines' object
/// and database-level-object builders (<c>Get-InventoryObjectQuerySql</c>,
/// <c>Get-InventoryDatabaseObjectQuerySql</c>) emit the same row shapes, and the delivered module splits them
/// with one function (<c>ConvertFrom-InventoryQueryRow</c>), so both engine adapters split them here.
/// </summary>
public static class DmsInventoryRows
{
    /// <summary>Splits a <c>schema|name|type</c> row as <c>ConvertFrom-InventoryQueryRow</c> does: exactly three fields, each non-blank after trimming.</summary>
    public static bool TryParseObjectRow(string? row, [NotNullWhen(true)] out DmsInventoryObject? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        string[] fields = row.Split('|');
        if (fields.Length != 3)
        {
            return false;
        }

        string schemaName = fields[0].Trim();
        string objectName = fields[1].Trim();
        string objectType = fields[2].Trim();
        if (schemaName.Length == 0 || objectName.Length == 0 || objectType.Length == 0)
        {
            return false;
        }

        entry = new DmsInventoryObject(schemaName, objectName, objectType);
        return true;
    }

    /// <summary>Splits a <c>kind|name</c> row; the name may itself contain the separator.</summary>
    public static bool TryParseDatabaseLevelObjectRow(
        string? row,
        [NotNullWhen(true)] out DmsDatabaseLevelObject? entry
    )
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        string[] fields = row.Trim().Split('|', 2);
        if (fields.Length != 2 || fields[0].Length == 0 || fields[1].Length == 0)
        {
            return false;
        }

        entry = new DmsDatabaseLevelObject(fields[0], fields[1]);
        return true;
    }
}
