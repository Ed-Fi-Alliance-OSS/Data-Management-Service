// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Strict subset of the shipped exporter's text contract, not a general Prometheus parser.</summary>
internal static partial class CdcConnectorTelemetryMetrics
{
    private const string Prefix = "edfi_cdc_source_lag_";

    internal static (
        CoreCdc.CdcConnectorLagObservation Lag,
        CdcConnectorTelemetryStatistics Statistics
    ) Parse(string text, CdcTelemetryObservationPass pass, DateTimeOffset startedAt)
    {
        string[] lines = text.Split(
            '\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
        );
        // HTTP 200 alone does not prove a successful JMX collection.
        Require(Scalar(lines, "jmx_scrape_error").Equals(0d));
        Require(Scalar(lines, "edfi_cdc_worker_start_time_seconds") > 0);
        var request = pass.Request;
        string provider =
            request.Binding.Provider == CoreCdc.CdcProvider.Postgresql ? "postgres" : "sql_server";
        long current =
            Lag(lines, "current", provider, request.Binding.ConnectorName)
            ?? throw new InvalidDataException();
        var p50 = Value(lines, "p50", provider, request.Binding.ConnectorName);
        var p95 = Value(lines, "p95", provider, request.Binding.ConnectorName);
        var p99 = Value(lines, "p99", provider, request.Binding.ConnectorName);
        // Inconsistent optional evidence has no effect on required current lag.
        if (p50 > p95 || p95 > p99 || p50 > p99)
        {
            p50 = null;
            p95 = null;
            p99 = null;
        }
        var minimum = Value(lines, "min", provider, request.Binding.ConnectorName);
        var maximum = Value(lines, "max", provider, request.Binding.ConnectorName);
        var average = Value(lines, "average", provider, request.Binding.ConnectorName);
        if (minimum > maximum || minimum > average || average > maximum)
        {
            minimum = null;
            maximum = null;
            average = null;
        }
        CoreCdc.CdcConnectorLagObservation observation = new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            pass.OperationId,
            startedAt,
            request.TargetIdentity,
            request.Binding.Provider,
            request.Binding.PhysicalSourceFingerprint,
            current <= pass.ThresholdMilliseconds
                ? CoreCdc.CdcConnectorLagState.WithinThreshold
                : CoreCdc.CdcConnectorLagState.Exceeded,
            current,
            pass.ThresholdMilliseconds,
            Milliseconds(p50),
            Milliseconds(p95),
            Milliseconds(p99),
            []
        );
        return (observation, new(minimum, maximum, average));
    }

    private static long? Lag(string[] lines, string statistic, string provider, string connector)
    {
        return Milliseconds(Value(lines, statistic, provider, connector));
    }

    private static long? Milliseconds(double? value)
    {
        // Core exposes integer milliseconds. Round upward conservatively, never understate lag.
        return value is not null && value < 9223372036854775808d
            ? checked((long)Math.Ceiling(value.Value))
            : null;
    }

    private static double? Value(string[] lines, string statistic, string provider, string connector)
    {
        string name = Prefix + statistic + "_milliseconds";
        if (!HasGaugeType(lines, name))
        {
            return null;
        }
        List<double> values = [];
        foreach (string line in lines.Where(line => IsSample(line, name)))
        {
            var sample = Sample().Match(line);
            if (!sample.Success)
            {
                return null;
            }
            var labels = Labels().Matches(sample.Groups["labels"].Value);
            Dictionary<string, string> parsed = new(StringComparer.Ordinal);
            int end = 0;
            foreach (Match label in labels)
            {
                if (
                    label.Index != end
                    || !parsed.TryAdd(label.Groups["key"].Value, label.Groups["value"].Value)
                )
                {
                    return null;
                }
                end = label.Index + label.Length;
            }
            if (
                end != sample.Groups["labels"].Length
                || !parsed.TryGetValue("connector", out var actualConnector)
            )
            {
                return null;
            }
            if (actualConnector != connector)
            {
                continue;
            }
            if (
                parsed.Count != 2
                || !parsed.TryGetValue("provider", out var actualProvider)
                || actualProvider != provider
                || !Number(sample.Groups["value"].Value, out double value)
            )
            {
                return null;
            }
            values.Add(value);
        }
        return values.Count == 1 ? values[0] : null;
    }

    private static double Scalar(string[] lines, string name)
    {
        Require(HasGaugeType(lines, name));
        string[] samples = lines.Where(line => IsSample(line, name)).ToArray();
        Require(samples.Length == 1 && samples[0].StartsWith(name + " ", StringComparison.Ordinal));
        Require(Number(samples[0][(name.Length + 1)..], out double value));
        return value;
    }

    private static bool IsSample(string line, string name) =>
        line == name
        || line.StartsWith(name + "{", StringComparison.Ordinal)
        || line.StartsWith(name + " ", StringComparison.Ordinal)
        || line.StartsWith(name + "\t", StringComparison.Ordinal);

    private static bool HasGaugeType(string[] lines, string name) =>
        lines.Count(line => line.StartsWith("# TYPE " + name + " ", StringComparison.Ordinal)) == 1
        && lines.Contains("# TYPE " + name + " gauge", StringComparer.Ordinal);

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value)
        && value >= 0;

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException();
        }
    }

    [GeneratedRegex(
        "^[a-zA-Z_:][a-zA-Z0-9_:]*\\{(?<labels>.*)\\}\\s+(?<value>[^\\s]+)$",
        RegexOptions.NonBacktracking
    )]
    private static partial Regex Sample();

    // Qualified binding/provider tokens contain no escapes. A peer's valid escaped label remains separate.
    [GeneratedRegex(
        "(?<key>[a-zA-Z_][a-zA-Z0-9_]*)=\"(?<value>(?:[^\"\\\\]|\\\\[\\\\\"n])*)\"(?:,|$)",
        RegexOptions.NonBacktracking
    )]
    private static partial Regex Labels();
}
