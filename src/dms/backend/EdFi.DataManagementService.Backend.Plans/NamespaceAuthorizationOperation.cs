// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// The CRUD operation a namespace authorization plan is being built for.
/// Drives which value-source checks the planner emits.
/// </summary>
public enum NamespaceAuthorizationOperation
{
    /// <summary>GET-by-id: stored-value check only.</summary>
    ReadSingle,

    /// <summary>GET-many: stored-value filter only.</summary>
    ReadMany,

    /// <summary>
    /// PUT and POST (create or upsert-as-update): stored-value check then proposed-value check. POST
    /// resolves as either a create or an upsert-as-update, so both write verbs plan the same
    /// stored-then-proposed pair rather than a create-only proposed check.
    /// </summary>
    Update,

    /// <summary>DELETE: stored-value check only.</summary>
    Delete,
}
