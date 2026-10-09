// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using NuGet.Common;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using NuGetLogLevel = NuGet.Common.LogLevel;

namespace EdFi.DataManagementService.ApiSchemaDownloader.Services;

internal sealed class NuGetLoggerAdapter(Microsoft.Extensions.Logging.ILogger logger) : LoggerBase
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Log(ILogMessage message)
    {
        _logger.Log(ToLogLevel(message.Level), "{NuGetMessage}", message.Message);
    }

    public override Task LogAsync(ILogMessage message)
    {
        Log(message);
        return Task.CompletedTask;
    }

    private static MsLogLevel ToLogLevel(NuGetLogLevel level) =>
        level switch
        {
            NuGetLogLevel.Debug => MsLogLevel.Debug,
            NuGetLogLevel.Verbose => MsLogLevel.Trace,
            NuGetLogLevel.Information => MsLogLevel.Information,
            NuGetLogLevel.Minimal => MsLogLevel.Information,
            NuGetLogLevel.Warning => MsLogLevel.Warning,
            NuGetLogLevel.Error => MsLogLevel.Error,
            _ => MsLogLevel.Information,
        };
}
