// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// A mapping from a query field name to the document paths the query field applies to
/// </summary>
internal record QueryField(
    /// <summary>
    /// The query field name
    /// </summary>
    string QueryFieldName,
    /// <summary>
    /// The document paths the query field applies to, along with types
    /// </summary>
    JsonPathAndType[] DocumentPathsWithType
);
