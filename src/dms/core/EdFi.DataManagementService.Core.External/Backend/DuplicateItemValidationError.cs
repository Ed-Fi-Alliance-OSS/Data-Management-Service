// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// The duplicate-item validation error shared by Core's array uniqueness validation and the backend's
/// storage-resolved duplicate check, so both return the same key and message.
/// </summary>
public static class DuplicateItemValidationError
{
    /// <summary>
    /// Builds a validation error tuple for array duplicate validation
    /// </summary>
    /// <param name="arrayPath">The JSONPath to the array containing duplicates</param>
    /// <param name="index">The index of the duplicate item</param>
    /// <returns>A tuple containing the error key and error message</returns>
    public static (string errorKey, string message) Build(string arrayPath, int index)
    {
        if (!arrayPath.Contains("[*]"))
        {
            throw new InvalidOperationException(
                $"Array validation failure: Array path {arrayPath} in ApiSchema.json must contain '[*]' to identify the array."
            );
        }

        string errorKey = arrayPath.Substring(0, arrayPath.IndexOf("[*]", StringComparison.Ordinal));
        string[] parts = errorKey.Split('.');
        string shortArrayName = parts[^1];
        string message =
            $"The {GetOrdinal(index + 1)} item of the {shortArrayName} has the same identifying values as another item earlier in the list.";
        return (errorKey, message);
    }

    /// <summary>
    /// Converts an ordinal number to its string representation (1st, 2nd, 3rd, etc.)
    /// </summary>
    /// <param name="number">The number to convert</param>
    /// <returns>The ordinal string representation</returns>
    private static string GetOrdinal(int number)
    {
        if (number % 100 == 11 || number % 100 == 12 || number % 100 == 13)
        {
            return $"{number}th";
        }

        return (number % 10) switch
        {
            1 => $"{number}st",
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th",
        };
    }
}
