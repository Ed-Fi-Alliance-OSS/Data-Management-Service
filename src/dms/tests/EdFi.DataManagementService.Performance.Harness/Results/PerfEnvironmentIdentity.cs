// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// A named value inside the environment identity, such as one database server setting or one
/// driver version.
/// </summary>
public sealed record PerfSetting(string Name, string Value);

/// <summary>
/// Identity of the measured database server: version, pinned image, storage caveats, the
/// connection-string shape (pooling and prepare settings with every secret redacted), and the
/// settings snapshot that shapes plans and timing.
/// </summary>
public sealed record PerfServerIdentity(
    string ServerVersion,
    string ImageTag,
    string ImageDigest,
    string StorageNote,
    string ConnectionStringShape,
    IReadOnlyList<PerfSetting> Settings
)
{
    /// <summary>
    /// Sorts the settings by name so the serialized artifact is deterministic regardless of
    /// capture order.
    /// </summary>
    public static PerfServerIdentity Create(
        string serverVersion,
        string imageTag,
        string imageDigest,
        string storageNote,
        string connectionStringShape,
        IEnumerable<PerfSetting> settings
    ) =>
        new(
            serverVersion,
            imageTag,
            imageDigest,
            storageNote,
            connectionStringShape,
            [.. settings.OrderBy(setting => setting.Name, StringComparer.Ordinal)]
        );
}

/// <summary>
/// Identity of the machine and runtime that produced the measurement. The machine fingerprint
/// is a stable pseudonym rather than a hostname, so committed artifacts can prove two runs
/// shared an environment without publishing the environment itself.
/// </summary>
public sealed record PerfHostIdentity(
    string OsDescription,
    string ProcessArchitecture,
    string CpuModel,
    int LogicalCores,
    long TotalMemoryBytes,
    string DotnetVersion,
    bool ServerGc,
    string MachineFingerprint
);

/// <summary>
/// The full environment identity recorded in the run manifest.
/// </summary>
public sealed record PerfEnvironmentIdentity(
    PerfServerIdentity Server,
    PerfHostIdentity Host,
    IReadOnlyList<PerfSetting> DriverVersions
)
{
    /// <summary>
    /// Sorts the driver versions by name so the serialized artifact is deterministic.
    /// </summary>
    public static PerfEnvironmentIdentity Create(
        PerfServerIdentity server,
        PerfHostIdentity host,
        IEnumerable<PerfSetting> driverVersions
    ) => new(server, host, [.. driverVersions.OrderBy(driver => driver.Name, StringComparer.Ordinal)]);
}
