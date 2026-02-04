// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Represents the idempotent DDL creation pattern used by a dialect for
/// programmable objects (functions, views, triggers).
/// </summary>
public enum DdlPattern
{
    /// <summary>
    /// PostgreSQL-style: CREATE OR REPLACE FUNCTION/VIEW.
    /// </summary>
    CreateOrReplace,

    /// <summary>
    /// SQL Server-style: CREATE OR ALTER FUNCTION/VIEW/TRIGGER.
    /// </summary>
    CreateOrAlter,

    /// <summary>
    /// Drop-then-create pattern for objects that do not support idempotent creation
    /// (e.g., PostgreSQL triggers).
    /// </summary>
    DropThenCreate,
}
