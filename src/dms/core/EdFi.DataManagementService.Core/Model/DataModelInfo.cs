// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// An API request sent from the frontend to be processed
/// </summary>
internal record DataModelInfo(
    /// <summary>
    /// The project name for this data model
    /// </summary>
    string ProjectName,
    /// <summary>
    /// The project version for this data model, in SemVer format
    /// </summary>
    string ProjectVersion,
    /// <summary>
    /// The description of this data model
    /// </summary>
    string Description
) : IDataModelInfo;
