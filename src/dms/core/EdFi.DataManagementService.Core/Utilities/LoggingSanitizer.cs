// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Utility class for sanitizing strings in logging to prevent log injection attacks
/// </summary>
public static class LoggingSanitizer
{
    /// <summary>
    /// Sanitizes input strings to prevent log injection attacks by removing or encoding potentially dangerous characters
    /// </summary>
    /// <param name="input">The input string to sanitize</param>
    /// <returns>A sanitized string safe for logging</returns>
    public static string SanitizeForLogging(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Early return optimization - check if sanitization is needed
        var needsSanitization = false;
        foreach (char c in input)
        {
            if (c is '\r' or '\n' or '\t' || (char.IsControl(c) && c != ' '))
            {
                needsSanitization = true;
                break;
            }
        }

        if (!needsSanitization)
        {
            // Even when no sanitization is required, return a new string instance to avoid returning user input by reference
            return new string(input);
        }

        // Only allocate StringBuilder if sanitization is actually needed
        var sanitized = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            switch (c)
            {
                case '\r' or '\n' or '\t':
                    sanitized.Append(' ');
                    break;
                default:
                    if (char.IsControl(c) && c != ' ')
                    {
                        sanitized.Append('?');
                    }
                    else
                    {
                        sanitized.Append(c);
                    }
                    break;
            }
        }
        return sanitized.ToString();
    }
}
