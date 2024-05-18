// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// The handler DMS Core uses to perform document queries.
/// </summary>
public interface IQueryHandler
{
    /// <summary>
    /// Entry point for query documents requests
    /// </summary>
    public Task<QueryResult> QueryDocuments(IQueryRequest queryRequest);
}
