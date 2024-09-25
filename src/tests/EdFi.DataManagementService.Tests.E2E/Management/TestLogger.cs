// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class TestLogger : IDisposable
{
    public Logger log;

    public TestLogger()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

        log = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            log.Dispose();
        }
    }
}
