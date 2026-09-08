// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Formats consistent context text for reference-mapping diagnostics.
/// </summary>
internal static class ReferenceMappingContextFormatter
{
    /// <summary>
    /// Builds a standard reference-mapping context prefix for invariant errors.
    /// </summary>
    public static string Build(DocumentReferenceMapping mapping, QualifiedResourceName resource)
    {
        return $"Reference mapping '{mapping.MappingKey}' on resource '{FormatResource(resource)}'";
    }
}
