// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>Engine settings for unit tests that never stage a file: valid values, a staging directory that is never written.</summary>
internal static class MssqlEngineSettingsSample
{
    public static MssqlStagingSettings Staging() =>
        new(
            Path.Combine(Path.GetTempPath(), "dms1438-unused-staging"),
            "/var/opt/dms-staging",
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            3,
            TimeSpan.FromSeconds(10)
        );

    public static MssqlDmsEngineSettings With(
        TimeSpan lockAcquireTimeout,
        TimeSpan lockHeartbeatInterval,
        TimeSpan? sessionTerminationTimeout = null,
        TimeSpan? restoreTimeout = null
    ) =>
        new(
            lockAcquireTimeout,
            lockHeartbeatInterval,
            sessionTerminationTimeout ?? TimeSpan.FromSeconds(30),
            restoreTimeout ?? TimeSpan.FromMinutes(5),
            Staging()
        );
}
