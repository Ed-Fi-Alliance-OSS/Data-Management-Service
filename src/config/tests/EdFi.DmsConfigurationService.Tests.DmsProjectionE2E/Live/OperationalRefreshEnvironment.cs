// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>
/// The operational Configuration Service <c>build-dms.ps1 InstanceE2ETest</c> recreates after provisioning each
/// tenant's reader credential: its education organization refresh interval, and a connection string that can only
/// read <c>dmscs.Tenant</c>, <c>dmscs.JobSchedule</c> and <c>dmscs.Job</c>. A missing or malformed value fails the
/// fixture with a setup message; nothing is skipped.
/// </summary>
public sealed class OperationalRefreshEnvironment
{
    private const string SetupFailure = "Operational refresh E2E setup failed";

    private OperationalRefreshEnvironment(int refreshIntervalMinutes, string cmsReadOnlyConnectionString)
    {
        RefreshIntervalMinutes = refreshIntervalMinutes;
        CmsReadOnlyConnectionString = cmsReadOnlyConnectionString;
    }

    public int RefreshIntervalMinutes { get; }

    /// <summary>Secret-bearing: never logged.</summary>
    public string CmsReadOnlyConnectionString { get; }

    public static OperationalRefreshEnvironment Read()
    {
        const string IntervalName = "INSTANCE_E2E_EDORG_REFRESH_INTERVAL_MINUTES";
        const string ConnectionName = "INSTANCE_E2E_CMS_READONLY_CONNECTION_STRING";

        if (
            !int.TryParse(
                Required(IntervalName),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int interval
            )
            || interval < 1
        )
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: '{IntervalName}' is not a positive integer."
            );
        }

        return new OperationalRefreshEnvironment(interval, Required(ConnectionName));
    }

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: required environment variable '{name}' is not set. Run this project through "
                    + "'./build-dms.ps1 InstanceE2ETest', which recreates the Configuration Service as an operational "
                    + "instance and exports these values to the test process."
            );
        }

        return value;
    }
}
