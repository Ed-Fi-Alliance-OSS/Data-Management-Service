// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// The grammar for the required-role token that gates a protected non-data endpoint. Every endpoint
/// that maps only when its role is configured shares this definition, so the set of accepted role
/// tokens cannot drift between them.
/// </summary>
public static class EndpointRequiredRole
{
    public const int MaximumLength = 256;

    public static bool IsValid([NotNullWhen(true)] string? requiredRole)
    {
        if (requiredRole is null || requiredRole.Length == 0)
        {
            return false;
        }

        if (requiredRole.Length > MaximumLength)
        {
            return false;
        }

        return !requiredRole.Any(IsInvalidCharacter);
    }

    private static bool IsInvalidCharacter(char character) =>
        character is <= '\u001f' or '\u007f' or ' ' or ',' or ';' or '"' or '\'' or '[' or ']' or '{' or '}'
        || char.IsWhiteSpace(character);
}
