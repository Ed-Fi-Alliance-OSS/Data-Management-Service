// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Tests.E2E.Extensions
{
    internal static class StringExtensions
    {
        /// <summary>
        /// Given a text with placeholders such as "My name is {name}",
        /// replaces the placeholders with the values found in the given dictionary.
        /// </summary>
        public static string ReplacePlaceholdersWithDictionaryValues(
            this string text,
            IReadOnlyDictionary<string, string> dictionary
        )
        {
            string placeholderPattern = @"\{(.*?)\}";

            string result = Regex.Replace(
                text,
                placeholderPattern,
                match =>
                {
                    string variableName = match.Groups[1].Value;
                    return dictionary.TryGetValue(variableName, out string? variableValue)
                        ? variableValue
                        : match.Value;
                }
            );

            return result;
        }
    }
}
