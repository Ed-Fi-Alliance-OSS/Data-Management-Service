// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConnectorOffsetMatchResult>))]
public enum CdcConnectorOffsetMatchResult
{
    Exact,
    Missing,
    Multiple,
    SourcePartitionMismatch,
}

public sealed record CdcProviderPositionComparisonResult
{
    private CdcProviderPositionComparisonResult(
        bool atOrBeyondBarrier,
        string? committedPosition,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        AtOrBeyondBarrier = atOrBeyondBarrier;
        CommittedPosition = committedPosition;
        Diagnostics = diagnostics;
    }

    public bool AtOrBeyondBarrier { get; }

    public string? CommittedPosition { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => AtOrBeyondBarrier && Diagnostics.Count == 0;

    public static CdcProviderPositionComparisonResult Success(string committedPosition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(committedPosition);

        return new(true, committedPosition, []);
    }

    public static CdcProviderPositionComparisonResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(false, null, diagnostics);
    }
}

public sealed record CdcPostgresqlWalPositionResult
{
    private CdcPostgresqlWalPositionResult(
        CdcPostgresqlWalPosition? position,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        Position = position;
        Diagnostics = diagnostics;
    }

    public CdcPostgresqlWalPosition? Position { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => Position is not null && Diagnostics.Count == 0;

    public static CdcPostgresqlWalPositionResult Success(CdcPostgresqlWalPosition position) =>
        new(position, []);

    public static CdcPostgresqlWalPositionResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, diagnostics);
    }
}

public readonly record struct CdcPostgresqlWalPosition(ulong Value) : IComparable<CdcPostgresqlWalPosition>
{
    public int CompareTo(CdcPostgresqlWalPosition other) => Value.CompareTo(other.Value);

    public override string ToString() => $"{Value >> 32:X}/{(uint)Value:X}";
}

public sealed record CdcPostgresqlConnectorOffset(
    CdcConnectorOffsetMatchResult SourcePartitionMatchResult,
    bool IsSnapshot,
    bool IsNull,
    long? LsnProc
);

public static class CdcPostgresqlProviderPosition
{
    public static CdcPostgresqlWalPositionResult ParseWalLsn(string? value, string path = "$.barrierLsn")
    {
        CdcDiagnosticCollector diagnostics = new();

        if (string.IsNullOrEmpty(value))
        {
            diagnostics.MissingRequiredField(path, "barrierLsn");
            return CdcPostgresqlWalPositionResult.Failure(diagnostics.Diagnostics);
        }

        int separatorIndex = value.IndexOf('/', StringComparison.Ordinal);
        if (
            separatorIndex <= 0
            || separatorIndex == value.Length - 1
            || value.IndexOf('/', separatorIndex + 1) >= 0
            || !TryParseHexUInt32(value[..separatorIndex], out uint high)
            || !TryParseHexUInt32(value[(separatorIndex + 1)..], out uint low)
        )
        {
            diagnostics.MalformedPayload(path, "CDC PostgreSQL WAL LSN must use `X/Y` hex format.");
            return CdcPostgresqlWalPositionResult.Failure(diagnostics.Diagnostics);
        }

        return CdcPostgresqlWalPositionResult.Success(new(((ulong)high << 32) | low));
    }

    public static CdcProviderPositionComparisonResult CompareCommittedOffsetToBarrier(
        CdcPostgresqlWalPosition barrier,
        CdcPostgresqlConnectorOffset offset
    )
    {
        ArgumentNullException.ThrowIfNull(offset);

        CdcDiagnosticCollector diagnostics = new();
        ValidateConnectorOffsetState(offset, diagnostics);

        if (offset.LsnProc is null)
        {
            diagnostics.MissingRequiredField("$.lsnProc", "lsnProc");
        }

        if (diagnostics.HasDiagnostics || offset.LsnProc is null)
        {
            return CdcProviderPositionComparisonResult.Failure(diagnostics.Diagnostics);
        }

        CdcPostgresqlWalPosition committedPosition = new(unchecked((ulong)offset.LsnProc.Value));
        if (committedPosition.CompareTo(barrier) < 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                "$.lsnProc",
                "CDC PostgreSQL connector offset lsnProc has not reached the provider barrier."
            );
            return CdcProviderPositionComparisonResult.Failure(diagnostics.Diagnostics);
        }

        return CdcProviderPositionComparisonResult.Success(committedPosition.ToString());
    }

    private static void ValidateConnectorOffsetState(
        CdcPostgresqlConnectorOffset offset,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcConnectorOffsetValidationRules.ValidateSourcePartitionMatch(
            offset.SourcePartitionMatchResult,
            diagnostics
        );
        CdcConnectorOffsetValidationRules.ValidateSnapshotFlag(offset.IsSnapshot, diagnostics);
        CdcConnectorOffsetValidationRules.ValidateNullFlag(offset.IsNull, diagnostics);
    }

    private static bool TryParseHexUInt32(string value, out uint parsed)
    {
        parsed = 0;
        return value.Length is >= 1 and <= 8
            && value.All(IsHex)
            && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool IsHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

public sealed record CdcSqlServerLsnResult
{
    private CdcSqlServerLsnResult(CdcSqlServerLsn? lsn, IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        Lsn = lsn;
        Diagnostics = diagnostics;
    }

    public CdcSqlServerLsn? Lsn { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => Lsn is not null && Diagnostics.Count == 0;

    public static CdcSqlServerLsnResult Success(CdcSqlServerLsn lsn) => new(lsn, []);

    public static CdcSqlServerLsnResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, diagnostics);
    }
}

public readonly record struct CdcSqlServerLsn(uint First, uint Second, ushort Third)
    : IComparable<CdcSqlServerLsn>
{
    public int CompareTo(CdcSqlServerLsn other)
    {
        int firstComparison = First.CompareTo(other.First);
        if (firstComparison != 0)
        {
            return firstComparison;
        }

        int secondComparison = Second.CompareTo(other.Second);
        return secondComparison != 0 ? secondComparison : Third.CompareTo(other.Third);
    }

    public override string ToString() => $"{First:x8}:{Second:x8}:{Third:x4}";
}

public sealed record CdcSqlServerProviderPosition(
    CdcSqlServerLsn CommitLsn,
    CdcSqlServerLsn ChangeLsn,
    ulong EventSerialNo
)
{
    public static CdcSqlServerProviderPosition HeartbeatAfterImage(
        CdcSqlServerLsn commitLsn,
        CdcSqlServerLsn changeLsn
    ) => new(commitLsn, changeLsn, 2);

    public int CompareTo(CdcSqlServerProviderPosition other)
    {
        ArgumentNullException.ThrowIfNull(other);

        int commitComparison = CommitLsn.CompareTo(other.CommitLsn);
        if (commitComparison != 0)
        {
            return commitComparison;
        }

        int changeComparison = ChangeLsn.CompareTo(other.ChangeLsn);
        return changeComparison != 0 ? changeComparison : EventSerialNo.CompareTo(other.EventSerialNo);
    }

    public override string ToString() => $"{CommitLsn}/{ChangeLsn}/{EventSerialNo}";
}

public sealed record CdcSqlServerConnectorOffset(
    CdcConnectorOffsetMatchResult SourcePartitionMatchResult,
    bool IsSnapshot,
    bool IsNull,
    string? CommitLsn,
    string? ChangeLsn,
    long? EventSerialNo
);

public static class CdcSqlServerProviderPositionParser
{
    public static CdcSqlServerLsnResult ParseLsn(string? value, string path)
    {
        CdcDiagnosticCollector diagnostics = new();

        if (string.IsNullOrEmpty(value))
        {
            diagnostics.MissingRequiredField(path, FieldNameFromPath(path));
            return CdcSqlServerLsnResult.Failure(diagnostics.Diagnostics);
        }

        string normalizedValue = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        if (TryParseContiguousHex(normalizedValue, out CdcSqlServerLsn contiguousLsn))
        {
            return CdcSqlServerLsnResult.Success(contiguousLsn);
        }

        if (TryParseDebeziumLsn(normalizedValue, out CdcSqlServerLsn debeziumLsn))
        {
            return CdcSqlServerLsnResult.Success(debeziumLsn);
        }

        diagnostics.MalformedPayload(
            path,
            "CDC SQL Server LSN must be 10 bytes encoded as `xxxxxxxx:xxxxxxxx:xxxx` or 20 hex digits."
        );
        return CdcSqlServerLsnResult.Failure(diagnostics.Diagnostics);
    }

    public static CdcSqlServerLsnResult NormalizeTenByteLsn(byte[]? value, string path)
    {
        CdcDiagnosticCollector diagnostics = new();

        if (value is null)
        {
            diagnostics.MissingRequiredField(path, FieldNameFromPath(path));
            return CdcSqlServerLsnResult.Failure(diagnostics.Diagnostics);
        }

        if (value.Length != 10)
        {
            diagnostics.MalformedPayload(path, "CDC SQL Server LSN must contain exactly 10 bytes.");
            return CdcSqlServerLsnResult.Failure(diagnostics.Diagnostics);
        }

        uint first = ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
        uint second = ((uint)value[4] << 24) | ((uint)value[5] << 16) | ((uint)value[6] << 8) | value[7];
        ushort third = (ushort)((value[8] << 8) | value[9]);

        return CdcSqlServerLsnResult.Success(new(first, second, third));
    }

    public static CdcProviderPositionComparisonResult CompareCommittedOffsetToBarrier(
        CdcSqlServerProviderPosition barrier,
        CdcSqlServerConnectorOffset offset
    )
    {
        ArgumentNullException.ThrowIfNull(barrier);
        ArgumentNullException.ThrowIfNull(offset);

        CdcDiagnosticCollector diagnostics = new();
        CdcConnectorOffsetValidationRules.ValidateSourcePartitionMatch(
            offset.SourcePartitionMatchResult,
            diagnostics
        );
        CdcConnectorOffsetValidationRules.ValidateSnapshotFlag(offset.IsSnapshot, diagnostics);
        CdcConnectorOffsetValidationRules.ValidateNullFlag(offset.IsNull, diagnostics);

        CdcSqlServerLsn? commitLsn = ParseOffsetLsn(offset.CommitLsn, "$.commitLsn", diagnostics);
        CdcSqlServerLsn? changeLsn = ParseOffsetLsn(offset.ChangeLsn, "$.changeLsn", diagnostics);
        ulong? eventSerialNo = ParseEventSerialNo(offset.EventSerialNo, diagnostics);

        if (diagnostics.HasDiagnostics || commitLsn is null || changeLsn is null || eventSerialNo is null)
        {
            return CdcProviderPositionComparisonResult.Failure(diagnostics.Diagnostics);
        }

        CdcSqlServerProviderPosition committedPosition = new(
            commitLsn.Value,
            changeLsn.Value,
            eventSerialNo.Value
        );
        if (committedPosition.CompareTo(barrier) < 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                "$.commitLsn",
                "CDC SQL Server connector offset has not reached the provider barrier."
            );
            return CdcProviderPositionComparisonResult.Failure(diagnostics.Diagnostics);
        }

        return CdcProviderPositionComparisonResult.Success(committedPosition.ToString());
    }

    private static CdcSqlServerLsn? ParseOffsetLsn(
        string? value,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcSqlServerLsnResult result = ParseLsn(value, path);
        foreach (CdcDiagnostic diagnostic in result.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        return result.Lsn;
    }

    private static ulong? ParseEventSerialNo(long? eventSerialNo, CdcDiagnosticCollector diagnostics)
    {
        if (eventSerialNo is null)
        {
            diagnostics.MissingRequiredField("$.eventSerialNo", "eventSerialNo");
            return null;
        }

        return unchecked((ulong)eventSerialNo.Value);
    }

    private static bool TryParseContiguousHex(string value, out CdcSqlServerLsn lsn)
    {
        lsn = default;
        if (value.Length != 20 || !value.All(IsHex))
        {
            return false;
        }

        lsn = new(
            uint.Parse(value[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            uint.Parse(value[8..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ushort.Parse(value[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        );
        return true;
    }

    private static bool TryParseDebeziumLsn(string value, out CdcSqlServerLsn lsn)
    {
        lsn = default;
        string[] parts = value.Split(':');
        if (
            parts.Length != 3
            || parts[0].Length != 8
            || parts[1].Length != 8
            || parts[2].Length != 4
            || Array.Exists(parts, part => !part.All(IsHex))
        )
        {
            return false;
        }

        lsn = new(
            uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ushort.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        );
        return true;
    }

    private static bool IsHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string FieldNameFromPath(string path)
    {
        int separatorIndex = path.LastIndexOf('.');
        return separatorIndex >= 0 && separatorIndex < path.Length - 1 ? path[(separatorIndex + 1)..] : path;
    }
}

public sealed record CdcSourcePartitionHashResult
{
    private CdcSourcePartitionHashResult(string? hash, IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        Hash = hash;
        Diagnostics = diagnostics;
    }

    public string? Hash { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => Hash is not null && Diagnostics.Count == 0;

    public static CdcSourcePartitionHashResult Success(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        return new(hash, []);
    }

    public static CdcSourcePartitionHashResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, diagnostics);
    }
}

public static class CdcSourcePartitionHashCalculator
{
    private const string PayloadPrefix = "ed-fi-dms-connect-source-partition-v1";
    private static readonly JsonWriterOptions CanonicalJsonWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CdcSourcePartitionHashResult ComputePostgresql(string? topicPrefix) =>
        Compute(CdcProvider.Postgresql, topicPrefix, null);

    public static CdcSourcePartitionHashResult ComputeSqlServer(
        string? topicPrefix,
        string? rawCatalogName
    ) => Compute(CdcProvider.SqlServer, topicPrefix, rawCatalogName);

    public static CdcSourcePartitionHashResult Compute(
        CdcProvider provider,
        string? topicPrefix,
        string? rawSqlServerCatalogName
    )
    {
        CdcDiagnosticCollector diagnostics = new();

        if (
            !CdcProviderToken.TryToRelationalProviderToken(
                provider,
                out RelationalProviderToken? providerToken
            )
        )
        {
            diagnostics.InvalidEnumValue("$.provider", "CDC provider must be `postgresql` or `sqlServer`.");
        }

        string? validatedTopicPrefix = CdcKafkaSafeTokenValidator.Validate(
            topicPrefix,
            "$.topicPrefix",
            "topicPrefix",
            diagnostics
        );

        if (provider == CdcProvider.SqlServer && string.IsNullOrEmpty(rawSqlServerCatalogName))
        {
            diagnostics.MissingRequiredField("$.database", "database");
        }

        if (diagnostics.HasDiagnostics || providerToken is null || validatedTopicPrefix is null)
        {
            return CdcSourcePartitionHashResult.Failure(diagnostics.Diagnostics);
        }

        byte[] canonicalJson = provider switch
        {
            CdcProvider.Postgresql => EncodePostgresqlSourcePartition(validatedTopicPrefix),
            CdcProvider.SqlServer => EncodeSqlServerSourcePartition(
                validatedTopicPrefix,
                rawSqlServerCatalogName!
            ),
            _ => [],
        };

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, PayloadPrefix);
        hash.AppendData([0]);
        AppendUtf8(hash, providerToken.Value);
        hash.AppendData([0]);
        hash.AppendData(canonicalJson);

        return CdcSourcePartitionHashResult.Success(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}"
        );
    }

    private static byte[] EncodePostgresqlSourcePartition(string topicPrefix)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer, CanonicalJsonWriterOptions);
        writer.WriteStartObject();
        writer.WriteString("server", topicPrefix);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeSqlServerSourcePartition(string topicPrefix, string rawCatalogName)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer, CanonicalJsonWriterOptions);
        writer.WriteStartObject();
        writer.WriteString("database", rawCatalogName);
        writer.WriteString("server", topicPrefix);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    private static void AppendUtf8(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}

internal static class CdcConnectorOffsetValidationRules
{
    public static void ValidateSourcePartitionMatch(
        CdcConnectorOffsetMatchResult matchResult,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(matchResult))
        {
            diagnostics.InvalidEnumValue(
                "$.sourcePartitionMatchResult",
                "CDC connector offset sourcePartitionMatchResult is unsupported."
            );
            return;
        }

        CdcDiagnosticCategory? category = matchResult switch
        {
            CdcConnectorOffsetMatchResult.Missing => CdcDiagnosticCategory.InvalidObservation,
            CdcConnectorOffsetMatchResult.Multiple => CdcDiagnosticCategory.InvalidObservation,
            CdcConnectorOffsetMatchResult.SourcePartitionMismatch => CdcDiagnosticCategory.SourceMismatch,
            _ => null,
        };

        if (category is not null)
        {
            diagnostics.Add(
                category.Value,
                "$.sourcePartitionMatchResult",
                "CDC connector offset must contain exactly one offset for the expected source partition."
            );
        }
    }

    public static void ValidateSnapshotFlag(bool isSnapshot, CdcDiagnosticCollector diagnostics)
    {
        if (isSnapshot)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.snapshot",
                "CDC connector offset must be committed source-position evidence, not a snapshot offset."
            );
        }
    }

    public static void ValidateNullFlag(bool isNull, CdcDiagnosticCollector diagnostics)
    {
        if (isNull)
        {
            diagnostics.MalformedPayload("$.offset", "CDC connector offset must not be null.");
        }
    }
}
