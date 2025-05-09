// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Extraction;

internal static class ArrayUniquenessConstraintExtractor
{
    public static Dictionary<string, JsonPath[]> ExtractUniquenessConstraints(
        this ResourceSchema resourceSchema,
        ILogger logger
    )
    {
        logger.LogDebug("ArrayUniquenessConstraintExtractor.ExtractUniquenessConstraints");

        return resourceSchema.ArrayUniquenessConstraints;
    }
}
