// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Api.Infrastructure
{
    // Example class for IOptions 
    public class LogAppSettingsService()
    {
        private readonly ILogger<LogAppSettingsService>? _logger;
        private readonly IOptions<AppSettings>? _options;

        public LogAppSettingsService(ILogger<LogAppSettingsService>? logger,
            IOptions<AppSettings>? options) : this()
        {
            _logger = logger;
            _options = options;
        }

        public void Log()
        {
            var appSettings = _options?.Value;
            _logger?.LogInformation("BeginAllowedSchoolYear = {year}.", appSettings?.BeginAllowedSchoolYear);
            _logger?.LogInformation("EndAllowedSchoolYear = {year}.", appSettings?.EndAllowedSchoolYear);
        }
    }
}
