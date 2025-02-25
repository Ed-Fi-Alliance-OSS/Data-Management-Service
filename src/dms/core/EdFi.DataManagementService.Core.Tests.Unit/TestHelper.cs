// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Tests.Unit;

public static class TestHelper
{
    /// <summary>
    /// Provides a no-op awaitable Next function
    /// </summary>
    public static readonly Func<Task> NullNext = () => Task.CompletedTask;

    /// <summary>
    /// Builds a ResourceSchema for the given endpointName on the given apiSchemaDocument
    /// </summary>
    internal static ResourceSchema BuildResourceSchema(
        ApiSchemaDocuments apiSchemaDocument,
        string endpointName,
        string projectNamespace = "ed-fi"
    )
    {
        ProjectSchema projectSchema = apiSchemaDocument.FindProjectSchemaForProjectNamespace(
            new(projectNamespace)
        )!;
        return new ResourceSchema(projectSchema.FindResourceSchemaNodeByEndpointName(new(endpointName))!);
    }
}
