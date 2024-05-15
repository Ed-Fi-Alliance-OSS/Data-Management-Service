// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// A string type branded as a MetaEdPropertyFullName, which is the full property name of a MetaEd
/// property on a MetaEd entity. Role names on a property are expressed by prefix on the property name.
/// </summary>
internal record struct MetaEdPropertyFullName(string Value);
