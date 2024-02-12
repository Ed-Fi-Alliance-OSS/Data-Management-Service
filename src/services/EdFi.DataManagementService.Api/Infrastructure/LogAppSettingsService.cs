// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace EdFi.DataManagementService.Api.Infrastructure
{
    // Example class for IOptions 
    public class LogAppSettingsService()
    {
        private readonly ILogger<LogAppSettingsService> _logger = null!;
        private readonly IOptions<AppSettings> _options = null!;

        public LogAppSettingsService(ILogger<LogAppSettingsService> logger,
            IOptions<AppSettings> options) : this()
        {
            _logger = logger;
            _options = options;
        }

        public void LogToConsole() => _logger.LogInformation(message: JsonSerializer.Serialize(_options?.Value));
    }
}
