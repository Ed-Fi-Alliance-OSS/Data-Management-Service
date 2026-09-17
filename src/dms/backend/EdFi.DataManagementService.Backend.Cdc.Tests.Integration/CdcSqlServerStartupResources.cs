// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private async Task<Dictionary<string, object>> ReadSqlServerStartupResourcesAsync(
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        CancellationToken token = timeout.Token;
        Dictionary<string, object> evidence = new()
        {
            ["HostState"] = "Unavailable",
            ["ContainerLimitsState"] = "Unavailable",
            ["ImageState"] = "Unavailable",
            ["DiskState"] = "Unavailable",
            // Docker removes the container cgroup after exit. Do not report a zero usage sample.
            ["ContainerUsageState"] = "UnavailableAfterExit",
        };
        if (OperatingSystem.IsLinux())
        {
            try
            {
                CdcSqlServerStartupResourceParser.AddHostMemory(
                    evidence,
                    await File.ReadAllTextAsync("/proc/meminfo", token)
                );
                foreach (
                    var (key, path) in new[]
                    {
                        ("HostPidMax", "/proc/sys/kernel/pid_max"),
                        ("HostThreadsMax", "/proc/sys/kernel/threads-max"),
                    }
                )
                {
                    if (long.TryParse((await File.ReadAllTextAsync(path, token)).Trim(), out long value))
                    {
                        evidence[key] = value;
                    }
                }
                string[] load = (await File.ReadAllTextAsync("/proc/loadavg", token)).Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries
                );
                if (load.Length >= 4 && long.TryParse(load[3].Split('/')[^1], out long tasks))
                {
                    evidence["HostTasks"] = tasks;
                }
                evidence["HostState"] = "Observed";
            }
            catch (Exception)
            {
                // Optional diagnostics must not replace the startup failure.
            }
        }
        try
        {
            var config = await _docker.RunAllowingFailureAsync(
                ["inspect", "--format", "{{json .HostConfig}}", ProviderContainerName],
                token
            );
            if (config.ExitCode == 0)
            {
                CdcSqlServerStartupResourceParser.AddContainerLimits(evidence, config.StandardOutput);
            }
        }
        catch (Exception)
        {
            // Retain unavailable metadata; never replace the startup error.
        }
        try
        {
            var root = await _docker.RunAllowingFailureAsync(
                ["info", "--format", "{{.DockerRootDir}}"],
                token
            );
            // Resolve the filesystem on the local Linux daemon host without publishing its path.
            if (root.ExitCode == 0 && OperatingSystem.IsLinux())
            {
                string path = root.StandardOutput.Trim();
                DriveInfo drive = DriveInfo
                    .GetDrives()
                    .Where(d =>
                        path == d.Name || path.StartsWith(d.Name.TrimEnd('/') + "/", StringComparison.Ordinal)
                    )
                    .OrderByDescending(d => d.Name.Length)
                    .First();
                evidence["DockerDiskAvailableBytes"] = drive.AvailableFreeSpace;
                evidence["DockerDiskTotalBytes"] = drive.TotalSize;
                evidence["DiskState"] = "Observed";
            }
        }
        catch (Exception)
        {
            // Retain unavailable metadata; never replace the startup error.
        }
        try
        {
            var image = await _docker.RunAllowingFailureAsync(
                [
                    "image",
                    "inspect",
                    "--format",
                    "{{index .Config.Labels \"com.microsoft.version\"}}",
                    _settings.ProviderImage,
                ],
                token
            );
            string version = image.StandardOutput.Trim();
            if (
                image.ExitCode == 0
                && Regex.IsMatch(
                    version,
                    @"\A[0-9]{1,3}(?:\.[0-9]{1,6}){3}\z",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)
                )
            )
            {
                evidence["SqlServerVersion"] = version;
                evidence["ImageState"] = "Observed";
            }
        }
        catch (Exception)
        {
            // Retain unavailable metadata; never replace the startup error.
        }
        return evidence;
    }
}

internal static class CdcSqlServerStartupResourceParser
{
    internal static void AddHostMemory(Dictionary<string, object> evidence, string text)
    {
        foreach (string key in new[] { "MemTotal", "MemAvailable", "SwapTotal", "SwapFree" })
        {
            Match match = Regex.Match(
                text,
                @"^" + key + @":\s+([0-9]+) kB$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            );
            if (
                match.Success
                && long.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long value
                )
            )
            {
                evidence["Host" + key + "KiB"] = value;
            }
        }
    }

    internal static void AddContainerLimits(Dictionary<string, object> evidence, string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (string key in new[] { "Memory", "MemorySwap", "NanoCpus", "PidsLimit" })
        {
            if (
                document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out long number)
            )
            {
                evidence["Container" + key] = number;
            }
        }
        if (
            document.RootElement.TryGetProperty("Ulimits", out var limits)
            && limits.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.GetProperty("Name").GetString() is "nproc" or "stack")
                {
                    string name = limit.GetProperty("Name").GetString()!;
                    evidence["Container" + name + "Soft"] = limit.GetProperty("Soft").GetInt64();
                    evidence["Container" + name + "Hard"] = limit.GetProperty("Hard").GetInt64();
                }
            }
        }
        evidence["ContainerLimitsState"] = "Observed";
    }
}
