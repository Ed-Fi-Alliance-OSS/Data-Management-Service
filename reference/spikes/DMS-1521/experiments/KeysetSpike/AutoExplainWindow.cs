// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KeysetSpike;

/// <summary>
/// PostgreSQL driver-path plans. For the length of one window, <c>auto_explain.log_min_duration</c> is
/// set to 0 with <c>ALTER SYSTEM</c> and <c>pg_reload_conf()</c>; the JSON, analyze, buffers, and
/// timing settings are preloaded by the run script. Pooled DMS sessions pick the change up at their
/// next statement. The plans are read back from <c>docker logs</c> between two <c>RAISE LOG</c>
/// markers, and the setting is reset on every exit path. Windows are kept apart from latency samples.
/// </summary>
public static partial class AutoExplainWindow
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("DMS1521_AUTO_EXPLAIN") is not { Length: > 0 } value
        || value.Trim() == "true";

    public static int Requests =>
        int.Parse(
            Environment.GetEnvironmentVariable("DMS1521_AUTO_EXPLAIN_REQUESTS") is { Length: > 0 } value
                ? value.Trim()
                : "20",
            CultureInfo.InvariantCulture
        );

    private static string Container =>
        Environment.GetEnvironmentVariable("DMS1521_PG_CONTAINER") is { Length: > 0 } value
            ? value.Trim()
            : "dms-1521-postgresql";

    /// <summary>
    /// Runs <paramref name="work"/> with auto_explain on, then writes
    /// <c>autoexplain/&lt;label&gt;.log</c> (the raw log between the markers) and
    /// <c>autoexplain/&lt;label&gt;.plans.json</c> (each plan with its backend pid and duration).
    /// </summary>
    public static async Task CaptureAsync(
        Func<Task<DbConnection>> openConnectionAsync,
        RunArtifacts run,
        string label,
        Func<Task> work
    )
    {
        string begin = $"DMS1521-AUTOEXPLAIN-BEGIN {label} {Guid.NewGuid():N}";
        string end = begin.Replace("-BEGIN ", "-END ", StringComparison.Ordinal);
        DateTime since = DateTime.UtcNow.AddSeconds(-30);

        try
        {
            await SetAsync(openConnectionAsync, "0");
            await RaiseLogAsync(openConnectionAsync, begin);
            await work();
            await RaiseLogAsync(openConnectionAsync, end);
        }
        finally
        {
            await SetAsync(openConnectionAsync, null);
        }

        string log = await ReadDockerLogsAsync(since);
        int start = log.IndexOf(begin, StringComparison.Ordinal);
        int stop = log.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < start)
        {
            throw new InvalidOperationException(
                $"auto_explain window {label}: markers not found in docker logs of {Container}."
            );
        }

        string window = log[start..stop];
        run.WriteText($"autoexplain/{label}.log", window);
        JsonArray plans = Parse(window);
        if (plans.Count == 0)
        {
            throw new InvalidOperationException($"auto_explain window {label}: no plans were logged.");
        }

        run.WriteJson($"autoexplain/{label}.plans.json", plans);
    }

    /// <summary>Sets the duration threshold (null resets it to the server's start-up value), reloads, and waits until a fresh session sees it.</summary>
    private static async Task SetAsync(Func<Task<DbConnection>> openConnectionAsync, string? value)
    {
        await using DbConnection connection = await openConnectionAsync();
        await ExecuteAsync(
            connection,
            value is null
                ? "ALTER SYSTEM RESET auto_explain.log_min_duration;"
                : $"ALTER SYSTEM SET auto_explain.log_min_duration = {value};"
        );
        await ExecuteAsync(connection, "SELECT pg_reload_conf();");

        string expected = value ?? "-1";
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            await using DbConnection probe = await openConnectionAsync();
            await using DbCommand command = probe.CreateCommand();
            command.CommandText = "SHOW auto_explain.log_min_duration;";
            string actual = Convert.ToString(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture
            )!;
            if (actual == expected || actual == expected + "ms")
            {
                break;
            }

            if (waited.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new InvalidOperationException(
                    $"auto_explain.log_min_duration is '{actual}', expected '{expected}', after reload."
                );
            }

            await Task.Delay(200);
        }

        // Idle pooled backends apply the reload at their next statement; give the signal time to land.
        await Task.Delay(1000);
    }

    private static async Task RaiseLogAsync(Func<Task<DbConnection>> openConnectionAsync, string marker)
    {
        await using DbConnection connection = await openConnectionAsync();
        await ExecuteAsync(connection, $"DO $marker$ BEGIN RAISE LOG '{marker}'; END $marker$;");
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadDockerLogsAsync(DateTime sinceUtc)
    {
        ProcessStartInfo start = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("logs");
        start.ArgumentList.Add("--since");
        start.ArgumentList.Add(sinceUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Container);

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker logs {Container} failed: {await stderr}");
        }

        // PostgreSQL logs to stderr; docker keeps the streams apart, so both are read.
        return (await stdout) + (await stderr);
    }

    // A log line: "<timestamp> <zone> [<pid>] <LEVEL>:  <message>". Continuation lines start with a tab.
    [GeneratedRegex(@"^\S+ \S+ \S+ \[(\d+)\] (\w+):  (.*)$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"^duration: ([0-9.]+) ms  plan:\s*$")]
    private static partial Regex PlanHeader();

    /// <summary>Parses each auto_explain entry into { pid, durationMs, plan }.</summary>
    public static JsonArray Parse(string log)
    {
        JsonArray plans = [];
        string? pid = null;
        double duration = 0;
        StringBuilder? body = null;

        void Flush()
        {
            if (body is null)
            {
                return;
            }

            JsonObject entry = new() { ["pid"] = pid, ["durationMs"] = duration };
            try
            {
                entry["plan"] = JsonNode.Parse(body.ToString());
            }
            catch (System.Text.Json.JsonException)
            {
                entry["raw"] = body.ToString();
            }

            plans.Add(entry);
            body = null;
        }

        foreach (string rawLine in log.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            Match match = LogLine().Match(line);
            if (match.Success)
            {
                Flush();
                Match header = PlanHeader().Match(match.Groups[3].Value);
                if (match.Groups[2].Value == "LOG" && header.Success)
                {
                    pid = match.Groups[1].Value;
                    duration = double.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                    body = new StringBuilder();
                }

                continue;
            }

            body?.AppendLine(line.StartsWith('\t') ? line[1..] : line);
        }

        Flush();
        return plans;
    }
}
