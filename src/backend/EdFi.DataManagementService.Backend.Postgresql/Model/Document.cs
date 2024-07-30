// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Postgresql.Model;

/// <summary>
/// A row from the Documents table
/// </summary>
public record Document(
    /// <summary>
    /// The partition key for this Document
    /// </summary>
    short DocumentPartitionKey,
    /// <summary>
    /// The externally known UUID for this Document
    /// </summary>
    Guid DocumentUuid,
    /// <summary>
    /// The ResourceName for this document
    /// </summary>
    string ResourceName,
    /// <summary>
    /// The ResourceVersion for this document
    /// </summary>
    string ResourceVersion,
    /// <summary>
    /// The ProjectName for this document
    /// </summary>
    string ProjectName,
    /// <summary>
    /// The JSON API document itself
    /// </summary>
    JsonElement EdfiDoc,
    /// <summary>
    /// The datetime this document was created in the database
    /// </summary>
    DateTime? CreatedAt = null,
    /// <summary>
    /// The datetime this document was last modified in the database
    /// </summary>
    DateTime? LastModifiedAt = null,
    /// <summary>
    /// The autogenerated Id
    /// </summary>
    long? Id = null
);
