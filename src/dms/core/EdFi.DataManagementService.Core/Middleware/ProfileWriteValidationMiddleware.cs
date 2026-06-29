// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Preserves the profile write validation pipeline position while relational profile
/// write semantics are handled by <see cref="ProfileWritePipelineMiddleware" />.
/// </summary>
internal class ProfileWriteValidationMiddleware : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        await next();
    }
}
