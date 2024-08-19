// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Extensions
{
    internal static class DataTableRowExtensions
    {
        public static string Parse(this DataTableRow dataRow)
        {
            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            };

            var rowDict = new Dictionary<string, object>();
            foreach (var column in dataRow.Keys)
            {
                rowDict[column] = ConvertValueToCorrectType(dataRow[column]);
            }

            return JsonSerializer.Serialize(rowDict, options);
        }

        private static object ConvertValueToCorrectType(string value)
        {
            // When other data type treated as string (ex: CalenderCode: "255901107")
            if (value.StartsWith('"') && value.EndsWith('"'))
            {
                return value.Trim('"');
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                return intValue;

            if (
                decimal.TryParse(
                    value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var decimalValue
                )
            )
                return decimalValue;

            if (
                DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateTimeValue
                )
            )
                return dateTimeValue.Date.ToString("yyyy-MM-dd");

            if (bool.TryParse(value, out var boolValue))
                return boolValue;

            if (value.StartsWith("[") && value.EndsWith("]") || value.StartsWith("{") && value.EndsWith("}"))
            {
                using var document =
                    JsonDocument.Parse(value) ?? throw new Exception($"Error while parsing {value}");

                return document.RootElement.Clone();
            }

            return value;
        }
    }
}
