// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The students each PUT targets, one distinct student per iteration: warmup targets first,
/// then measured targets, so the two sets are disjoint and every measured PUT is a real
/// mutation rather than a repeat of an earlier one.
/// </summary>
public sealed record PerfWriteTargets(
    IReadOnlyList<long> WarmupOrdinals,
    IReadOnlyList<long> MeasuredOrdinals
)
{
    public int Count => WarmupOrdinals.Count + MeasuredOrdinals.Count;

    public IEnumerable<long> All => WarmupOrdinals.Concat(MeasuredOrdinals);

    /// <summary>
    /// The target of an absolute <see cref="PerfLatencyMeasurement" /> iteration.
    /// </summary>
    public long OrdinalFor(int iteration) =>
        iteration < WarmupOrdinals.Count
            ? WarmupOrdinals[iteration]
            : MeasuredOrdinals[iteration - WarmupOrdinals.Count];

    /// <summary>
    /// Deterministic stride selection with no RNG: ordinal 1, then every
    /// <c>rowCount / count</c>-th student, so the targets spread over the whole fixture.
    /// </summary>
    public static PerfWriteTargets Select(long rowCount, int warmupIterations, int measuredIterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(warmupIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(measuredIterations, 1);

        int count = warmupIterations + measuredIterations;
        if (count > rowCount)
        {
            throw new ArgumentException(
                $"{count} write targets need {count} distinct students; the fixture has {rowCount}."
            );
        }

        long stride = rowCount / count;
        long[] ordinals = [.. Enumerable.Range(0, count).Select(index => 1 + (index * stride))];
        return new PerfWriteTargets(ordinals[..warmupIterations], ordinals[warmupIterations..]);
    }
}

public sealed record PerfCommandCountFrequency(int CommandCount, int Requests);

public sealed record PerfWriteScenarioResult(
    string ScenarioId,
    int WarmupAttempted,
    int WarmupCompleted,
    int MeasuredAttempted,
    int MeasuredCompleted,
    int Failed,
    IReadOnlyList<PerfCommandCountFrequency> MeasuredCommandCounts,
    DateTimeOffset MeasuredWindowStartUtc,
    DateTimeOffset MeasuredWindowEndUtc,
    PerfLatencySummary LatencyMs
);

/// <summary>
/// Outcome accounting for the write loop. The caller owns it, so its exact counts survive a
/// failure that stops the loop. A request that throws stays attempted but not completed.
/// </summary>
public sealed class PerfWriteOutcomes(int warmupIterations, int measuredIterations)
{
    private readonly SortedDictionary<int, int> _measuredCommandCounts = [];

    public int WarmupAttempted { get; private set; }

    public int WarmupCompleted { get; private set; }

    public int MeasuredAttempted { get; private set; }

    public int MeasuredCompleted { get; private set; }

    public int Failed => WarmupAttempted + MeasuredAttempted - WarmupCompleted - MeasuredCompleted;

    public string? FailureReason { get; private set; }

    public DateTimeOffset? MeasuredWindowStartUtc { get; private set; }

    public DateTimeOffset? MeasuredWindowEndUtc { get; private set; }

    public IReadOnlyList<PerfCommandCountFrequency> MeasuredCommandCounts =>
        [.. _measuredCommandCounts.Select(entry => new PerfCommandCountFrequency(entry.Key, entry.Value))];

    /// <summary>
    /// Called before dispatch. The first measured attempt opens the measured window.
    /// </summary>
    public void OnAttempt(int iteration, DateTimeOffset utcNow)
    {
        if (iteration < warmupIterations)
        {
            WarmupAttempted++;
            return;
        }

        MeasuredAttempted++;
        if (iteration == warmupIterations)
        {
            MeasuredWindowStartUtc = utcNow;
        }
    }

    /// <summary>
    /// Called after the response is read. A status other than 204 or a missing etag records
    /// the failure and throws, which stops the loop; otherwise the request completes, and the
    /// last measured completion closes the measured window.
    /// </summary>
    public void OnResponse(
        int iteration,
        HttpStatusCode status,
        bool hasEtag,
        int commandCount,
        string body,
        DateTimeOffset utcNow
    )
    {
        string at = $"{PerfBenchmarkScenarios.StudentPutCollectionUpdate} iteration {iteration}";
        if (status != HttpStatusCode.NoContent)
        {
            Fail($"{at}: HTTP {(int)status}, expected 204: {body[..Math.Min(body.Length, 500)]}");
        }

        if (!hasEtag)
        {
            Fail($"{at}: the 204 response carried no etag.");
        }

        if (iteration < warmupIterations)
        {
            WarmupCompleted++;
            return;
        }

        MeasuredCompleted++;
        _measuredCommandCounts[commandCount] = _measuredCommandCounts.GetValueOrDefault(commandCount) + 1;
        if (iteration == warmupIterations + measuredIterations - 1)
        {
            MeasuredWindowEndUtc = utcNow;
        }
    }

    public PerfWriteScenarioResult ToResult(PerfLatencySummary latencyMs) =>
        new(
            PerfBenchmarkScenarios.StudentPutCollectionUpdate,
            WarmupAttempted,
            WarmupCompleted,
            MeasuredAttempted,
            MeasuredCompleted,
            Failed,
            MeasuredCommandCounts,
            MeasuredWindowStartUtc ?? throw new PerfObservationException("The measured window never opened."),
            MeasuredWindowEndUtc ?? throw new PerfObservationException("The measured window never closed."),
            latencyMs
        );

    private void Fail(string reason)
    {
        FailureReason = reason;
        throw new PerfObservationException(reason);
    }
}

/// <summary>
/// The serial Student collection-update scenario: each iteration PUTs one target's full
/// fixture document with only <c>otherNames[0].firstName</c> changed, an in-place collection
/// item update that keeps data volume and operation mix constant.
/// </summary>
public static class PerfWriteScenarioExecutor
{
    public const string UpdatedFirstName = "PerfUpdated";

    /// <summary>
    /// The four child collection tables, each holding one row per student.
    /// </summary>
    public static readonly IReadOnlyList<string> ChildCollectionTables =
    [
        "StudentIdentificationDocument",
        "StudentOtherName",
        "StudentPersonalIdentificationDocument",
        "StudentVisa",
    ];

    /// <summary>
    /// The fixture document of the student at <paramref name="ordinal" /> (the loader's row
    /// shape, as the fixture smoke's control POST writes it) with the single first-name change.
    /// </summary>
    public static JsonObject PayloadFor(long ordinal) =>
        new()
        {
            ["studentUniqueId"] = PerfFixtureDefinition.StudentUniqueIdFor(ordinal),
            ["firstName"] = PerfFixtureDefinition.FirstName,
            ["lastSurname"] = PerfFixtureDefinition.LastSurname,
            ["birthDate"] = PerfFixtureDefinition.BirthDateIso,
            ["birthSexDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                PerfFixtureDefinition.SexDescriptorResource
            ),
            ["otherNames"] = new JsonArray(
                new JsonObject
                {
                    ["otherNameTypeDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.OtherNameTypeDescriptorResource
                    ),
                    ["firstName"] = UpdatedFirstName,
                    ["lastSurname"] = PerfFixtureDefinition.LastSurname,
                }
            ),
            ["identificationDocuments"] = new JsonArray(IdentificationDocument()),
            ["personalIdentificationDocuments"] = new JsonArray(IdentificationDocument()),
            ["visas"] = new JsonArray(
                new JsonObject
                {
                    ["visaDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.VisaDescriptorResource
                    ),
                }
            ),
        };

    /// <summary>
    /// Runs the loop through <see cref="PerfLatencyMeasurement" />: payload preparation,
    /// command baselines and response checks all stay outside the timed window. The first
    /// failure stops the loop with exact counts in <paramref name="outcomes" />.
    /// </summary>
    public static async Task<PerfWriteScenarioResult> RunAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        PerfWriteTargets targets,
        PerfWriteOutcomes outcomes
    )
    {
        using DriverCommandObserver observer = DriverCommandObserver.Start(provider);
        string url = string.Empty;
        string payload = string.Empty;
        int commandBaseline = 0;
        HttpStatusCode status = default;
        bool hasEtag = false;
        string body = string.Empty;

        PerfLatencySummary latency = await PerfLatencyMeasurement.MeasureAsync(
            async _ =>
            {
                using StringContent content = new(payload, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await harness.HttpClient.PutAsync(url, content);
                status = response.StatusCode;
                hasEtag =
                    response.Headers.TryGetValues("ETag", out IEnumerable<string>? etags)
                    && etags.Any(etag => !string.IsNullOrWhiteSpace(etag));
                body = await response.Content.ReadAsStringAsync();
            },
            targets.WarmupOrdinals.Count,
            targets.MeasuredOrdinals.Count,
            beforeIterationAsync: iteration =>
            {
                long ordinal = targets.OrdinalFor(iteration);
                url =
                    $"{PerfFixtureDefinition.ResourceEndpoint}/{PerfFixtureDefinition.DocumentUuidFor(ordinal)}";
                payload = PayloadFor(ordinal).ToJsonString();
                outcomes.OnAttempt(iteration, DateTimeOffset.UtcNow);
                commandBaseline = observer.Commands.Count;
                return Task.CompletedTask;
            },
            afterIterationAsync: iteration =>
            {
                outcomes.OnResponse(
                    iteration,
                    status,
                    hasEtag,
                    observer.Commands.Count - commandBaseline,
                    body,
                    DateTimeOffset.UtcNow
                );
                return Task.CompletedTask;
            }
        );

        return outcomes.ToResult(latency);
    }

    /// <summary>
    /// Set-based proof that the writes happened: exactly one updated other-name row per
    /// target, none elsewhere, and every child table still at one row per student.
    /// </summary>
    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfProvider provider,
        PerfFixtureDefinition definition,
        PerfWriteTargets targets
    )
    {
        string Table(string name) =>
            provider == PerfProvider.Postgresql ? $"\"edfi\".\"{name}\"" : $"[edfi].[{name}]";
        string Column(string name) => provider == PerfProvider.Postgresql ? $"\"{name}\"" : $"[{name}]";

        string updated = $"{Column("FirstName")} = '{UpdatedFirstName}'";
        string targetDocumentIds = string.Join(
            ", ",
            targets.All.Select(ordinal =>
                PerfFixtureDefinition.DocumentIdFor(ordinal).ToString(CultureInfo.InvariantCulture)
            )
        );

        return
        [
            new(
                "updated-other-names-on-targets",
                $"SELECT COUNT(*) FROM {Table("StudentOtherName")} WHERE {updated} "
                    + $"AND {Column("Student_DocumentId")} IN ({targetDocumentIds});",
                targets.Count
            ),
            new(
                "updated-other-names-total",
                $"SELECT COUNT(*) FROM {Table("StudentOtherName")} WHERE {updated};",
                targets.Count
            ),
            .. ChildCollectionTables.Select(table => new PerfVerificationQuery(
                $"{table}-row-count",
                $"SELECT COUNT(*) FROM {Table(table)};",
                definition.RowCount
            )),
        ];
    }

    /// <summary>
    /// One mismatch per query whose observed count differs from its expected count; empty
    /// means the verification passed.
    /// </summary>
    public static IReadOnlyList<string> Evaluate(
        IEnumerable<(PerfVerificationQuery Query, long Actual)> observed
    ) =>
        [
            .. observed
                .Where(entry => entry.Actual != entry.Query.ExpectedValue)
                .Select(entry =>
                    $"{entry.Query.Name}: expected {entry.Query.ExpectedValue}, got {entry.Actual}."
                ),
        ];

    public static async Task<IReadOnlyList<string>> VerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfFixtureDefinition definition,
        PerfWriteTargets targets
    )
    {
        List<(PerfVerificationQuery, long)> observed = [];
        foreach (PerfVerificationQuery query in VerificationQueries(provider, definition, targets))
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = query.Sql;
            object? value = await command.ExecuteScalarAsync();
            observed.Add((query, Convert.ToInt64(value, CultureInfo.InvariantCulture)));
        }

        return Evaluate(observed);
    }

    private static JsonObject IdentificationDocument() =>
        new()
        {
            ["identificationDocumentUseDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
            ),
            ["personalInformationVerificationDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
            ),
        };
}
