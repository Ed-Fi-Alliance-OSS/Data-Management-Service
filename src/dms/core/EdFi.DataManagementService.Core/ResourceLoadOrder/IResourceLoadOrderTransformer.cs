// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// Provides a mean to modify the resulting <see cref="LoadOrder"/> list.
/// </summary>
internal interface IResourceLoadOrderTransformer
{
    /// <summary>
    /// A hook to modify the resulting <see cref="LoadOrder"/> <b>after</b> topological sorting has been executed.
    /// </summary>
    void Transform(IList<LoadOrder> resources);
}
