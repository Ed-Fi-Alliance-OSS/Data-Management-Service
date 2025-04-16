// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Frontend.BulkLoader.Model;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.BulkLoader.Processor
{
    public static class SchoolYearProcessor
    {
        public static async Task ProcessSchoolYearTypesAsync(ILogger _logger,
            SchoolYearTypesWrapper model,
            IApiService apiService,
            string token)
        {
            if (model?.SchoolYearTypes == null || model.SchoolYearTypes.Count == 0)
            {
                _logger.LogError(" No school year types found in the JSON");
                throw new InvalidOperationException("No school year types found in the JSON.");
            }

            int currentYear = DateTime.UtcNow.Month > 6
                ? DateTime.UtcNow.Year + 1
                : DateTime.UtcNow.Year;

            model.SchoolYearTypes.ForEach(s => s.currentSchoolYear = s.schoolYear == currentYear);

            foreach (var item in model.SchoolYearTypes)
            {
                var payload = JsonSerializer.Serialize(item, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var request = new FrontendRequest(
                    new("/ed-fi/schoolYearTypes/"),
                    payload,
                    [],
                    new TraceId(""),
                    new ClientAuthorizations(
                        TokenId: token,
                        ClaimSetName: "BootstrapDescriptorsandEdOrgs",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                );

                var response = await apiService.Upsert(request);
                if (response.StatusCode != 200 && response.StatusCode != 201)
                {
                    _logger.LogError(
                        "Failed to upsert school year type {SchoolYear} with status code {StatusCode}",
                        item.schoolYear,
                        response.StatusCode);
                    throw new InvalidOperationException($"Failed to upsert school year type {item.schoolYear}");
                }
                _logger.LogInformation(
                    "Successfully upserted school year type {SchoolYear} with status code {StatusCode}",
                    item.schoolYear,
                    response.StatusCode);
                Console.WriteLine($"Successfully upserted school year type  {item.schoolYear} with status code {response.StatusCode}");
            }
        }
    }
}
