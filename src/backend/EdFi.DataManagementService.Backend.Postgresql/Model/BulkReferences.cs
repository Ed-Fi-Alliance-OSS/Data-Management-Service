// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Postgresql.Model;

/// <summary>
/// A set of rows for insert into the References table
/// </summary>
public record BulkReferences(
    /// <summary>
    /// Id from the Documents table for the document these references are on
    /// </summary>
    long ParentDocumentId,
    /// <summary>
    /// The partition key from the Documents table for the document these references are on
    /// </summary>
    short ParentDocumentPartitionKey,
    /// <summary>
    /// The ReferentialIds on the Aliases table for all the references on this document
    /// </summary>
    Guid[] ReferentialIds,
    /// <summary>
    /// The ReferentialPartitionKeys on the Aliases table for all the references on this document
    /// </summary>
    short[] ReferentialPartitionKeys
);
