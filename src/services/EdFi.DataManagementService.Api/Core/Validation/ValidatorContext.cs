// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;

namespace EdFi.DataManagementService.Api.Core.Validation
{
    /// <summary>
    /// Validator context contains ResourceJsonSchema and RequestActionMethod to use with schema generation
    /// </summary>
    /// <param name="ResourceJsonSchema"></param>
    /// <param name="RequestActionMethod"></param>
    public record ValidatorContext(ResourceSchema ResourceJsonSchema, RequestMethod RequestActionMethod);
}
