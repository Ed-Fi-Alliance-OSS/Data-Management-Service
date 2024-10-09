// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;

public static class DmsLoggerExtensions
{
    public static bool IsInfoEnabled(this ILogger logger)
    {
        return logger.IsEnabled(LogLevel.Information);
    }

    public static bool IsDebugEnabled(this ILogger logger)
    {
        return logger.IsEnabled(LogLevel.Debug);
    }
}
