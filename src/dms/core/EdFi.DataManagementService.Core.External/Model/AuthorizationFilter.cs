// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Represents an authorization filter with a specified
/// JSON element name for extracting data, and the expected value
/// </summary>
public record AuthorizationFilter(
    /// <summary>
    /// The JSON element name used to extract the relevant data
    /// </summary>
    string FilterPath,
    /// <summary>
    /// The expected value used
    /// </summary>
    string Value
);
