// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model
{
    /// <summary>
    /// A pair of JsonPaths, the value of which must be equal in an Ed-Fi API JSON document
    /// </summary>
    internal record EqualityConstraint(
        JsonPath SourceJsonPath,
        JsonPath TargetJsonPath);
}
