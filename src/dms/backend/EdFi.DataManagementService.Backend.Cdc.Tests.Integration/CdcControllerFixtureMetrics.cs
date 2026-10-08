// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Records only metric shape and numeric classification from the response consumed by DMS.</summary>
internal sealed class CdcControllerFixtureMetrics : DelegatingHandler
{
    private readonly ConcurrentQueue<object> _observations = new();
    public IReadOnlyList<object> Observations => _observations.ToArray();

    public CdcControllerFixtureMetrics()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false }) { }

    internal CdcControllerFixtureMetrics(HttpMessageHandler inner)
        : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            await response.Content.LoadIntoBufferAsync(
                CdcConnectorTelemetryAdapter.MaximumResponseBytes,
                cancellationToken
            );
            Record(await response.Content.ReadAsStringAsync(cancellationToken));
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    internal void Record(string text)
    {
        const string metric = "edfi_cdc_source_lag_current_milliseconds";
        string[] lines = text.Split(
            '\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
        );
        string[] samples = lines
            .Where(line => line.StartsWith(metric + "{", StringComparison.Ordinal))
            .ToArray();
        _observations.Enqueue(
            new
            {
                At = DateTimeOffset.UtcNow,
                GaugeTypeCount = lines.Count(line => line == "# TYPE " + metric + " gauge"),
                SampleCount = samples.Length,
                Samples = samples
                    .Take(8)
                    .Select(line =>
                    {
                        bool parsed = double.TryParse(
                            line[(line.LastIndexOf(' ') + 1)..],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double value
                        );
                        return new
                        {
                            Parsed = parsed,
                            Finite = parsed && double.IsFinite(value),
                            Negative = parsed && value < 0,
                            Uninitialized = parsed && value.Equals(-1d),
                            Overflow = parsed && value >= 9223372036854775808d,
                        };
                    })
                    .ToArray(),
            }
        );
        while (_observations.Count > 128)
        {
            _observations.TryDequeue(out _);
        }
    }
}
