// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Shared naming rules for tracked-change value columns.
/// </summary>
public static class TrackedChangeNameConventions
{
    public static DbColumnName OldValueColumn(DbColumnName sourceColumn) => new("Old" + sourceColumn.Value);

    public static DbColumnName NewValueColumn(DbColumnName sourceColumn) => new("New" + sourceColumn.Value);
}
