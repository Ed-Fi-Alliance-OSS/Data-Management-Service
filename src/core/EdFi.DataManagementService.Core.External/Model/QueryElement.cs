// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend.Model;

/// <summary>
/// A single element of a query. A single API client query term can
/// map to multiple paths of a document. If that is the case,
/// a query handler should "OR" those paths together.
/// </summary>
public record QueryElement(
    /// <summary>
    /// The query field name provide by the API client
    /// </summary>
    string QueryFieldName,
    /// <summary>
    /// The document paths the query field applies to
    /// </summary>
    JsonPath[] DocumentPaths,
    /// <summary>
    /// The value being searched for
    /// </summary>
    string Value
);
