// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The name/key of a route qualifier (e.g., "district", "schoolYear")
/// </summary>
public record struct RouteQualifierName(string Value);

/// <summary>
/// The value of a route qualifier (e.g., "255901", "2024")
/// </summary>
public record struct RouteQualifierValue(string Value);
