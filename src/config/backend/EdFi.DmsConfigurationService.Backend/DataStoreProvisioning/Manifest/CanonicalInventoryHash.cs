// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

/// <summary>
/// The canonical inventory hash of the delivered restore contract (F-P8, <c>Get-CanonicalInventoryHash</c>):
/// the lowercase hex SHA-256 of the UTF-8 bytes of <see cref="CanonicalInventoryJson.Serialize"/>. It is the
/// value a manifest records as <c>inventorySha256</c> and the value recomputed from a database's catalog, so
/// it is the only comparison between two inventories.
/// </summary>
public static class CanonicalInventoryHash
{
    public static string Compute(CanonicalInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        byte[] canonicalBytes = Encoding.UTF8.GetBytes(CanonicalInventoryJson.Serialize(inventory));
        return Convert.ToHexStringLower(SHA256.HashData(canonicalBytes));
    }
}
