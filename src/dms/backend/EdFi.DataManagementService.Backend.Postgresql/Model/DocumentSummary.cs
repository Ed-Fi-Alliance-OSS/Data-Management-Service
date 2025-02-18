// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Postgresql.Model;

/// <summary>
/// A subset of a document row with only the document itself and the last
/// modified date and trace id
/// </summary>
public record DocumentSummary(
    /// <summary>
    /// The JSON document itself
    /// </summary>
    JsonElement EdfiDoc,
    /// <summary>
    /// The SecurityElements JSON field from the database
    /// </summary>
    JsonElement SecurityElements,
    /// <summary>
    /// The datetime this document was last modified in the database
    /// </summary>
    DateTime LastModifiedAt,
    /// <summary>
    /// The correlation id of the last insert or update
    /// </summary>
    string LastModifiedTraceId
);
