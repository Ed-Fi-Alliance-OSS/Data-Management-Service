// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

public static partial class ManagedDataStoreNameFormatter
{
    // Pinned upstream ed115fd8, documented in reference/design/managed-data-stores-DMS-1439/contract-provenance.md.
    [GeneratedRegex(@"^(?:(?:edfi_+ods)(?:_+|$))+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Prefix();

    public static DmsTargetDatabaseName Format(
        string name,
        DmsDatabaseTemplate template,
        ProtectedDatabaseNames protectedNames
    )
    {
        if (!CatalogName.IsValidManagedName(name))
        {
            throw new ArgumentException("Invalid managed data store name.", nameof(name));
        }
        string remaining = Prefix().Replace(CatalogName.Trim(name).Replace(' ', '_').Trim('_'), "").Trim('_');
        string candidate =
            remaining.Length == 0
                ? $"EdFi_Ods_{template.WireValue}"
                : $"EdFi_Ods_{remaining}_{template.WireValue}";
        if (
            DmsTargetDatabaseName.TryCreate(candidate, protectedNames, out var target)
            != DmsTargetNameValidation.Valid
        )
        {
            throw new ArgumentException("Invalid or protected managed target database name.", nameof(name));
        }
        return target;
    }
}
