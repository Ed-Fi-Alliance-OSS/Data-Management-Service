// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal static class MssqlGeneratedDdlModelLookup
{
    /// <summary>
    /// Every table that stores document stamps: each relational resource root, plus the shared
    /// <c>dms.Descriptor</c> table. Since Phase 4 these rows own <c>ContentVersion</c>,
    /// <c>IdentityVersion</c> and their timestamps outright, so a fixture holding only a
    /// <c>DocumentId</c> reads its stamps by selecting across this set.
    /// </summary>
    public static IReadOnlyList<(string Schema, string Table)> EnumerateStampTables(
        DerivedRelationalModelSet modelSet
    )
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        return
        [
            ("dms", "Descriptor"),
            .. modelSet
                .ConcreteResourcesInNameOrder.Where(static resource =>
                    resource.StorageKind == ResourceStorageKind.RelationalTables
                )
                .Select(static resource =>
                    (
                        resource.RelationalModel.Root.Table.Schema.Value,
                        resource.RelationalModel.Root.Table.Name
                    )
                )
                .Distinct(),
        ];
    }
}
