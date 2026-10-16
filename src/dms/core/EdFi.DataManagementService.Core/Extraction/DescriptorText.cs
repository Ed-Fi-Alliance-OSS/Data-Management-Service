// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// The text rule for descriptor identity values: well-formed UTF-16 with no NUL character. Request
/// validation applies <see cref="IsWellFormedWithoutNul"/> and answers with a path-attributed 400;
/// descriptor reference normalization applies <see cref="AssertWellFormedWithoutNul"/> before it
/// lowercases and hashes a value. Public because the backend's descriptor query-filter validation
/// applies the same rule.
/// </summary>
public static class DescriptorText
{
    /// <summary>
    /// The client-facing message for a value that breaks the rule. It never echoes the value.
    /// </summary>
    public const string InvalidValueMessage =
        "Descriptor values must not contain the NUL character (U+0000) or unpaired surrogate characters.";

    /// <summary>
    /// Returns true when the value contains no NUL character and every surrogate is part of a valid pair.
    /// </summary>
    public static bool IsWellFormedWithoutNul(string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index];

            if (character == '\0' || char.IsLowSurrogate(character))
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                // Skip the low surrogate that completes the pair.
                index++;
            }

            index++;
        }

        return true;
    }

    /// <summary>
    /// Returns the value when it is well-formed without NUL. Throws when it is not, because request
    /// validation must have rejected it before normalization.
    /// </summary>
    public static string AssertWellFormedWithoutNul(string value) =>
        IsWellFormedWithoutNul(value)
            ? value
            : throw new InvalidOperationException(
                "A descriptor value reached normalization without passing descriptor text validation."
            );
}
