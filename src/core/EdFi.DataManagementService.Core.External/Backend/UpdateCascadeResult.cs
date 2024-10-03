// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// The resulting data from a single cascading update iteration
/// </summary>
public record UpdateCascadeResult(
    /// <summary>
    /// The EdFiDoc without updates applied for use in lookups
    /// </summary>
    JsonNode OriginalEdFiDoc,
    /// <summary>
    /// The EdFiDoc with updates applied
    /// </summary>
    JsonNode ModifiedEdFiDoc,
    /// <summary>
    /// The Id of the referencing resource
    /// </summary>
    long Id,
    /// <summary>
    /// The partition key of the referencing resource
    /// </summary>
    short DocumentPartitionKey,
    /// <summary>
    /// The documentuuid of the referencing resource
    /// </summary>
    Guid DocumentUuid,
    /// <summary>
    /// The project name of the referencing resource
    /// </summary>
    string ProjectName,
    /// <summary>
    /// The name of the referencing resource
    /// </summary>
    string ResourceName,
    /// <summary>
    /// Identifies whether this update was itself an identity update
    /// requiring a recursive cascading update
    /// </summary>
    bool isIdentityUpdate
);
