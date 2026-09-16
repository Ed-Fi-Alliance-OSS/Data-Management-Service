// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Records bounded shape evidence from offsets consumed by the real adapter.</summary>
internal sealed class CdcControllerFixtureOffsets : DelegatingHandler
{
    private readonly ConcurrentQueue<object> _observations = new();
    public IReadOnlyList<object> Observations => _observations.ToArray();

    public CdcControllerFixtureOffsets()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false }) { }

    internal CdcControllerFixtureOffsets(HttpMessageHandler inner)
        : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (
            request.Method != HttpMethod.Get
            || request.RequestUri is null
            || !request.RequestUri.AbsolutePath.EndsWith("/offsets", StringComparison.Ordinal)
            || response.StatusCode != HttpStatusCode.OK
        )
        {
            return response;
        }

        // Observe reads, rather than buffering ahead of the adapter's size and cancellation checks.
        var original = response.Content;
        try
        {
            var stream = await original.ReadAsStreamAsync(cancellationToken);
            var content = new StreamContent(new EvidenceStream(stream, original, this));
            foreach (var header in original.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Content = content;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    internal void Record(ReadOnlyMemory<byte> bytes, string completion)
    {
        if (completion != "Complete")
        {
            Enqueue(new { At = DateTimeOffset.UtcNow, Completion = completion });
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            var root = document.RootElement;
            var offsets = Field(root, "offsets");
            Enqueue(
                new
                {
                    At = DateTimeOffset.UtcNow,
                    Completion = completion,
                    RootKind = root.ValueKind.ToString(),
                    OffsetsKind = offsets.ValueKind.ToString(),
                    Count = offsets.ValueKind == JsonValueKind.Array ? offsets.GetArrayLength() : 0,
                    Entries = offsets.ValueKind == JsonValueKind.Array
                        ? offsets.EnumerateArray().Take(8).Select(Shape).ToArray()
                        : [],
                }
            );
        }
        catch (JsonException)
        {
            Enqueue(new { At = DateTimeOffset.UtcNow, Completion = "InvalidJson" });
        }
    }

    private void Enqueue(object observation)
    {
        _observations.Enqueue(observation);
        while (_observations.Count > 128)
        {
            _observations.TryDequeue(out _);
        }
    }

    private static object Shape(JsonElement entry)
    {
        var partition = Field(entry, "partition");
        var offset = Field(entry, "offset");
        var snapshot = Field(offset, "snapshot");
        string snapshotText = snapshot.ValueKind == JsonValueKind.String ? snapshot.GetString()! : "";
        var serial = Field(offset, "event_serial_no");
        bool serialInteger = serial.ValueKind == JsonValueKind.Number && serial.TryGetInt64(out _);
        return new
        {
            EntryKind = entry.ValueKind.ToString(),
            PartitionKind = partition.ValueKind.ToString(),
            PartitionFieldCount = partition.ValueKind == JsonValueKind.Object
                ? partition.EnumerateObject().Count()
                : 0,
            ServerKind = Field(partition, "server").ValueKind.ToString(),
            DatabaseKind = Field(partition, "database").ValueKind.ToString(),
            ServerNonEmpty = CdcConnectOffsetEvidence.TryString(partition, "server", out _),
            DatabaseNonEmpty = CdcConnectOffsetEvidence.TryString(partition, "database", out _),
            OffsetKind = offset.ValueKind.ToString(),
            SnapshotKind = snapshot.ValueKind.ToString(),
            SnapshotFalse = snapshot.ValueKind == JsonValueKind.False || snapshotText == "false",
            SnapshotActive = snapshot.ValueKind == JsonValueKind.True
                || snapshotText
                    is "true"
                        or "last"
                        or "incremental"
                        or "INITIAL"
                        or "BLOCKING"
                        or "INCREMENTAL",
            Commit = Lsn(Field(offset, "commit_lsn")),
            Change = Lsn(Field(offset, "change_lsn")),
            SerialKind = serial.ValueKind.ToString(),
            SerialInteger = serialInteger,
            SerialNonNegative = serialInteger && serial.GetInt64() >= 0,
            SerialZero = serialInteger && serial.GetInt64() == 0,
            SerialOne = serialInteger && serial.GetInt64() == 1,
        };
    }

    private static object Lsn(JsonElement value)
    {
        string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        return new
        {
            Kind = value.ValueKind.ToString(),
            NullMarker = text == "NULL",
            Valid = value.ValueKind == JsonValueKind.String
                && CdcSqlServerProviderPositionParser.ParseLsn(text, "$.offset").Succeeded,
        };
    }

    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            ? field
            : default;

    private sealed class EvidenceStream(Stream inner, HttpContent original, CdcControllerFixtureOffsets owner)
        : Stream
    {
        private readonly MemoryStream _bytes = new();
        private bool _recorded;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private void Capture(ReadOnlySpan<byte> bytes)
        {
            if (_recorded)
            {
                return;
            }
            if (_bytes.Length + bytes.Length > CdcConnectRestAdapter.MaximumResponseBytes)
            {
                _recorded = true;
                owner.Record(default, "TooLarge");
            }
            else if (bytes.IsEmpty)
            {
                _recorded = true;
                owner.Record(_bytes.GetBuffer().AsMemory(0, (int)_bytes.Length), "Complete");
            }
            else
            {
                _bytes.Write(bytes);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (count > 0)
            {
                Capture(buffer.AsSpan(offset, read));
            }
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            if (!buffer.IsEmpty)
            {
                Capture(buffer.Span[..read]);
            }
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_recorded)
                {
                    _recorded = true;
                    owner.Record(default, "Incomplete");
                }
                _bytes.Dispose();
                original.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
