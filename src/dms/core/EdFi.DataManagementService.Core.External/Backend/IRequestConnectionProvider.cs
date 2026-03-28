// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Provides the request-scoped database connection selection for backend consumers.
/// </summary>
public interface IRequestConnectionProvider
{
    /// <summary>
    /// Gets the request-scoped connection information for the selected DMS instance.
    /// </summary>
    RequestConnection GetRequestConnection();
}
