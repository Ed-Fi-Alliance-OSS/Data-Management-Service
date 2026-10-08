// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// A point in the DMS container's output. Docker keeps stdout and stderr as separate streams, so a
/// position counts each separately; a later snapshot is then compared stream by stream rather than by
/// an interleaving the two streams do not preserve.
/// </summary>
public readonly record struct DmsLogPosition(int StdoutLines, int StderrLines);

/// <summary>
/// What <c>docker logs</c> returned for the DMS container, one stream at a time.
/// </summary>
public sealed record DmsLogSnapshot(IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr)
{
    public DmsLogPosition Position => new(Stdout.Count, Stderr.Count);

    /// <summary>
    /// Every line of both streams.
    /// </summary>
    public IEnumerable<string> AllLines => Stdout.Concat(Stderr);

    /// <summary>
    /// The lines written after <paramref name="position"/> in either stream.
    /// </summary>
    public IEnumerable<string> LinesSince(DmsLogPosition position) =>
        Stdout.Skip(position.StdoutLines).Concat(Stderr.Skip(position.StderrLines));
}

/// <summary>
/// Reads the logs of, and restarts, the DMS container the Instance Management stack runs. Every
/// <c>docker</c> call passes its arguments through <see cref="ProcessStartInfo.ArgumentList"/>, so no
/// shell parses them.
/// </summary>
public static class DmsContainerControl
{
    private static readonly TimeSpan DockerCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HealthDeadline = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The container's current <c>State.StartedAt</c>.
    /// </summary>
    public static async Task<string> GetStartedAtAsync()
    {
        (int exitCode, string stdout, string stderr) = await RunDockerAsync(
            "inspect",
            "-f",
            "{{.State.StartedAt}}",
            TestConfiguration.DmsContainerName
        );

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker inspect of container '{TestConfiguration.DmsContainerName}' failed with exit code {exitCode}: {stderr.Trim()}"
            );
        }

        return stdout.Trim();
    }

    /// <summary>
    /// The container's logs so far.
    /// </summary>
    public static async Task<DmsLogSnapshot> GetLogsAsync()
    {
        (int exitCode, string stdout, string stderr) = await RunDockerAsync(
            "logs",
            TestConfiguration.DmsContainerName
        );

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker logs of container '{TestConfiguration.DmsContainerName}' failed with exit code {exitCode}: {stderr.Trim()}"
            );
        }

        // docker logs replays the container's own stderr on stderr, so the two streams stay apart.
        return new DmsLogSnapshot(SplitLines(stdout), SplitLines(stderr));
    }

    /// <summary>
    /// Restarts the container, proves it started again, and waits for <c>/health</c> to answer 200.
    /// </summary>
    public static async Task RestartAsync()
    {
        string startedAtBefore = await GetStartedAtAsync();

        (int exitCode, _, string stderr) = await RunDockerAsync(
            "restart",
            TestConfiguration.DmsContainerName
        );

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker restart of container '{TestConfiguration.DmsContainerName}' failed with exit code {exitCode}: {stderr.Trim()}"
            );
        }

        string startedAtAfter = await GetStartedAtAsync();

        if (string.Equals(startedAtBefore, startedAtAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Container '{TestConfiguration.DmsContainerName}' reports the same State.StartedAt after docker restart, so the restart is not proven."
            );
        }

        await WaitForHealthAsync();
    }

    private static async Task WaitForHealthAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < HealthDeadline)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(
                    $"{TestConfiguration.DmsApiUrl}/health"
                );
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener is not up yet.
            }
            catch (TaskCanceledException)
            {
                // The request timed out while the host was still starting.
            }

            await Task.Delay(HealthPollInterval);
        }

        throw new TimeoutException(
            $"DMS did not answer 200 from /health within {HealthDeadline.TotalSeconds:F0} seconds after the restart."
        );
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerAsync(
        params string[] arguments
    )
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeout = new CancellationTokenSource(DockerCommandTimeout);

        process.Start();

        // Both streams are drained concurrently: a full pipe on one would block the process while the
        // other is being read.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"docker {arguments[0]} did not finish within {DockerCommandTimeout.TotalSeconds:F0} seconds."
            );
        }
    }
}
