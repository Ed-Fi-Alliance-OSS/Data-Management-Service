// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Extensions;

internal static class DataTableExtensions
{
    public static IEnumerable<Dictionary<string, object>> ExtractDescriptors(this DataTable dataTable)
    {
        var descriptors = new List<string>();

        // Use regex to extract descriptor namespaces in this format: "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
        var regex = @"\buri://.*\b";
        foreach (var row in dataTable.Rows)
        {
            foreach (var cell in row)
            {
                var m = Regex.Match(cell.Value, regex);
                if (m.Success)
                {
                    descriptors.Add(m.Value);
                }
            }
        }

        // then build the descriptor object with string splitting operations
        return descriptors.Distinct().Select(d =>
        {
            // eg: "GradeLevelDescriptors"
            var descriptorName = d.Split('#')[0][(d.LastIndexOf('/') + 1)..] + 's';
            // eg: "Tenth Grade"
            var codeValue = d.Split('#')[1];
            // eg: "uri://ed-fi.org/GradeLevelDescriptor"
            var namespaceName = d.Split('#')[0];

            return new Dictionary<string, object>()
            {
                { "descriptorName", descriptorName},
                { "codeValue",  codeValue},
                { "description", codeValue },
                { "namespace", namespaceName },
                { "shortDescription", codeValue }
            };
        });
    }
}
