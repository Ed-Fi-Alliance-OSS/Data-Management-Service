// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Model;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader.Processor
{
    public static class SchoolYearProcessor
    {
        public static async Task ProcessSchoolYearTypesAsync(
            ILogger _logger,
            IApiService apiService,
            string token,
            int startYear,
            int endYear)
        {

            int currentYear = DateTime.UtcNow.Month > 6
                ? DateTime.UtcNow.Year + 1
                : DateTime.UtcNow.Year;

            var schoolYearTypes = Enumerable.Range(startYear, endYear - startYear + 1)
                .Select(year => new SchoolYearType
                {
                    schoolYear = year,
                    currentSchoolYear = year == currentYear,
                    schoolYearDescription = $"{year - 1}-{year}"
                })
                .ToList();

            foreach (var item in schoolYearTypes)
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
                Console.WriteLine($"Successfully upserted school year type {item.schoolYear} with status code {response.StatusCode}");
            }
        }

    }
}
