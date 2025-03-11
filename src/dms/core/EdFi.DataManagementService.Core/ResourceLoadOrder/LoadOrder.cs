// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// Specifies the order in which a resource should be loaded based on its dependencies.
/// </summary>
/// <param name="Resource">The resource's endpoint name.</param>
/// <param name="Group">Number specifying the order in which the resource should be loaded.</param>
/// <param name="Operations">The allowed operations, such as "Create" or "Update".</param>
internal record LoadOrder(string Resource, int Group, IList<string> Operations);
