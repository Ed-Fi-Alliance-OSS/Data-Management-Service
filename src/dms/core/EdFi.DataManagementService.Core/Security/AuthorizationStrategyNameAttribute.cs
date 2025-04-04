// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Provides the authorization strategy name that is associated with an IAuthorizationValidator implementation
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuthorizationStrategyNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
