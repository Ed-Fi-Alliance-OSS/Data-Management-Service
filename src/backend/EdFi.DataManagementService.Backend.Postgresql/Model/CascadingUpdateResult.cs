// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Postgresql.Model;

/// <summary>
/// The result from the NpgSqlCommand that cascades updates via
/// the returning statement.
/// </summary>
public record CascadingUpdateResult(
    /// <summary>
    /// Id of the down stream document that was modified
    /// </summary>
    long ModifiedDocumentId,
    /// <summary>
    /// The partition key of the down stream document that was modified
    /// </summary>
    short ModifiedDocumentPartitionKey,
    /// <summary>
    /// The resource name of the down stream document that was modified
    /// for use in recursive cascading updates
    /// </summary>
    string ModifiedResourceName
);

public record ResourceIdentification(long DocumentId, short DocumentPartitionKey, string ResourceName);
