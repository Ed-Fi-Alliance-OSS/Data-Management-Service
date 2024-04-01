// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Core.Model
{
    /// <summary>
    /// A pair of JsonPaths, the value of which must be equal in an Ed-Fi API JSON document
    /// </summary>
    public record EqualityConstraint(
        /// <summary>
        /// 
        /// </summary>
        JsonPath SourceJsonPath,

        /// <summary>
        /// 
        /// </summary>
        JsonPath TargetJsonPath);
}
