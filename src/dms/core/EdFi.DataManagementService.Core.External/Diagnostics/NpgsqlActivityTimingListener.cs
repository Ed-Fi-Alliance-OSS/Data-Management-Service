// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace EdFi.DataManagementService.Core.External.Diagnostics;

/// <summary>
/// Listens to the ActivitySource built into Npgsql ("Npgsql"), which emits one activity
/// span per command execution once a listener is attached. This captures every SQL round
/// trip issued anywhere in the backend (session executors, hydrators, direct DbCommand
/// call sites) without touching each call site. Durations are attributed to the current
/// request's <see cref="RequestTiming"/> when one is ambient, and per-statement aggregate
/// stats (keyed by a normalized prefix of the SQL text, like pg_stat_statements) are
/// folded directly into <see cref="RequestTimingRegistry"/>.
/// </summary>
public static class NpgsqlActivityTimingListener
{
    private const string NpgsqlActivitySourceName = "Npgsql";
    private const string FirstResponseEventName = "received-first-response";
    private const int SqlTagMaxLength = 90;

    private static ActivityListener? _listener;

    /// <summary>Starts listening; idempotent.</summary>
    public static void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        ActivityListener listener = new()
        {
            ShouldListenTo = static source =>
                string.Equals(source.Name, NpgsqlActivitySourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };

        ActivitySource.AddActivityListener(listener);
        _listener = listener;
    }

    private static void OnActivityStopped(Activity activity)
    {
        double durationMs = activity.Duration.TotalMilliseconds;
        string sqlTag = NormalizeSqlTag(activity);

        RequestTiming? timing = RequestTimingContext.Current;
        if (timing is null)
        {
            // DB activity outside any API request (health checks, startup, background work).
            RequestTimingRegistry.Observe("Db.Command.NoRequest", durationMs);
        }
        else
        {
            string phase = timing.InDbSession
                ? RequestTimingRegistry.DbPhases.CommandInTxn
                : RequestTimingRegistry.DbPhases.Command;
            timing.RecordCompleted(phase, activity.Duration, sqlTag);
        }

        RequestTimingRegistry.Observe($"Sql: {sqlTag}", durationMs);
        ObserveFirstResponse(activity);
    }

    /// <summary>
    /// Npgsql adds a "received-first-response" event when the first byte of the server
    /// response arrives; the offset from span start approximates per-statement server
    /// latency + one network round trip, excluding row streaming and buffering.
    /// </summary>
    private static void ObserveFirstResponse(Activity activity)
    {
        ActivityEvent firstResponse = activity.Events.FirstOrDefault(e =>
            string.Equals(e.Name, FirstResponseEventName, StringComparison.Ordinal)
        );

        if (firstResponse.Timestamp != default)
        {
            double firstResponseMs = (firstResponse.Timestamp - activity.StartTimeUtc).TotalMilliseconds;
            RequestTimingRegistry.Observe("Db.Command.FirstResponse", firstResponseMs);
        }
    }

    /// <summary>
    /// Reduces a SQL text to a short, stable, whitespace-collapsed prefix suitable as an
    /// aggregation key. Statements are parameterized, so equal statements produce equal tags.
    /// </summary>
    private static string NormalizeSqlTag(Activity activity)
    {
        string? sql = activity.GetTagItem("db.statement") as string ?? activity.DisplayName;
        if (string.IsNullOrEmpty(sql))
        {
            return "(unknown)";
        }

        StringBuilder builder = new(SqlTagMaxLength + 1);
        bool previousWasSpace = false;
        foreach (char c in sql)
        {
            if (builder.Length >= SqlTagMaxLength)
            {
                builder.Append('…');
                break;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
            }
            else
            {
                builder.Append(c);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }
}
