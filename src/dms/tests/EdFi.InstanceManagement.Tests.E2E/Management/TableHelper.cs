// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Helper class for creating Reqnroll Table objects programmatically
/// </summary>
public static class TableHelper
{
    /// <summary>
    /// Create a single-row table with specified columns and values
    /// </summary>
    public static Table CreateTable(params (string column, string value)[] data)
    {
        var columns = data.Select(d => d.column).ToArray();
        var table = new Table(columns);
        var values = data.Select(d => d.value).ToArray();
        table.AddRow(values);
        return table;
    }
}
