// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Configuration;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader.Processor
{
    public static class SchoolYearProcessor
    {
        public static async Task ProcessSchoolYearTypesAsync(
            ILogger _logger,
            IApiService apiService,
            IConfigurationServiceTokenHandler tokenHandler,
            ConfigurationServiceSettings configSettings,
            int startYear,
            int endYear,
            int currentSchoolYear
        )
        {
            // Get JWT token for system operations
            _logger.LogInformation("Retrieving authentication token for school year loading");
            var accessToken = await tokenHandler.GetTokenAsync(
                configSettings.ClientId,
                configSettings.ClientSecret,
                configSettings.Scope
            );

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to retrieve authentication token");
                throw new InvalidOperationException("Failed to retrieve authentication token");
            }

            var schoolYearTypes = Enumerable
                .Range(startYear, endYear - startYear + 1)
                .Select(year => new SchoolYearType
                {
                    schoolYear = year,
                    currentSchoolYear = year == currentSchoolYear,
                    schoolYearDescription = $"{year - 1}-{year}",
                })
                .ToList();

            foreach (var item in schoolYearTypes)
            {
                var payload = JsonSerializer.Serialize(
                    item,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
                );

                var headers = new Dictionary<string, string> { { "Authorization", $"Bearer {accessToken}" } };

                var request = new FrontendRequest(
                    Path: "/ed-fi/schoolYearTypes/",
                    Body: payload,
                    Headers: headers,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

                var response = await apiService.Upsert(request);
                if (response.StatusCode != 200 && response.StatusCode != 201)
                {
                    _logger.LogError(
                        "Failed to upsert school year type {SchoolYear} with status code {StatusCode}",
                        item.schoolYear,
                        response.StatusCode
                    );
                    throw new InvalidOperationException(
                        $"Failed to upsert school year type {item.schoolYear}"
                    );
                }

                _logger.LogInformation(
                    "Successfully upserted school year type {SchoolYear} with status code {StatusCode}",
                    item.schoolYear,
                    response.StatusCode
                );
                Console.WriteLine(
                    $"Successfully upserted school year type {item.schoolYear} with status code {response.StatusCode}"
                );
            }
        }
    }
}
