// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts the StudentAuthorizationSecurableInfo from a resource
/// </summary>
internal static class StudentAuthorizationSecurableExtractor
{
    public static StudentAuthorizationSecurableInfo ExtractStudentAuthorizationSecurableInfo(
        this ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
        List<string> studentUniqueIds = [];
        studentUniqueIds.AddRange(
            resourceSchema.StudentAuthorizationSecurablePaths.Select(studentAuthorizationSecurablePath =>
                documentBody.SelectRequiredNodeFromPathCoerceToString(
                    studentAuthorizationSecurablePath.Value,
                    logger
                )
            )
        );

        if (studentUniqueIds.Distinct().Count() > 1)
        {
            throw new InvalidOperationException(
                "More than one distinct StudentUniqueId found on StudentAuthorizationSecurable document."
            );
        }

        return new StudentAuthorizationSecurableInfo(
            IsStudentAuthorizationSecurable: studentUniqueIds.Count > 0,
            StudentUniqueId: studentUniqueIds.FirstOrDefault()
        );
    }
}
