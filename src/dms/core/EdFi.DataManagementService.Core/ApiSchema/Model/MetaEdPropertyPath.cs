// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// A string type branded as a MetaEdPropertyPath, which is a dot-separated MetaEd property name list
/// denoting a path from a starting entity through other entities. Role names on a property
/// are expressed by prefix on the property name. Most commonly used as a merge directive path.
/// </summary>
internal record struct MetaEdPropertyPath(string Value);
