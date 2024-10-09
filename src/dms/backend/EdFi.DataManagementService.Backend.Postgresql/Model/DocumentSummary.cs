// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Postgresql.Model;

/// <summary>
/// Extracts only the document and the date and trace id it was last updated 
/// </summary>
public record DocumentSummary(
    JsonElement EdfiDoc,
    /// <summary>
    /// The datetime this document was created in the database
    /// </summary>
    /// <summary>
    /// The datetime this document was last modified in the database
    /// </summary>
    DateTime LastModifiedAt,
    /// <summary>
    /// The correlation id of the last insert or update
    /// </summary>
    string LastModifiedTraceId
);
