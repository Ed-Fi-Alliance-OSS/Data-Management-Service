// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Shared constants and helpers for data model identification.
/// </summary>
public static class DataModelConstants
{
    /// <summary>
    /// Returns whether the given project name identifies the core Ed-Fi data model.
    /// </summary>
    public static bool IsCoreProjectName(string projectName)
        => projectName.Equals("Ed-Fi", StringComparison.OrdinalIgnoreCase);
}
