// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend.Model;

/// <summary>
/// A query parameter for a single term
/// </summary>
public record TermQuery(
    /// <summary>
    /// The term field
    /// </summary>
    string Field,

    /// <summary>
    /// The term value
    /// </summary>
    string Value
);
