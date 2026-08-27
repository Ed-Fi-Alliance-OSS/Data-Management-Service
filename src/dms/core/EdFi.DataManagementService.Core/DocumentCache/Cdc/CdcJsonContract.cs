// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public interface ICdcJsonContract
{
    int ContractVersion { get; }
}

public static class CdcJsonContract
{
    public const int CurrentContractVersion = 1;

    private const string ContractVersionPropertyName = "contractVersion";

    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

        options.Converters.Add(new CdcLowerCamelJsonStringEnumConverterFactory());
        return options;
    }

    public static string Serialize<TContract>(TContract contract) =>
        JsonSerializer.Serialize(contract, SerializerOptions);

    public static CdcContractReadResult<TContract> Deserialize<TContract>(
        string payload,
        int expectedContractVersion = CurrentContractVersion
    )
    {
        ArgumentNullException.ThrowIfNull(payload);

        using JsonDocument? document = TryParsePayload(payload, out CdcDiagnostic? parseDiagnostic);
        if (parseDiagnostic is not null)
        {
            return CdcContractReadResult<TContract>.Failure([parseDiagnostic]);
        }

        CdcContractValidationResult versionResult = ValidateRequiredContractVersion(
            document!.RootElement,
            expectedContractVersion
        );
        if (!versionResult.Succeeded)
        {
            return CdcContractReadResult<TContract>.Failure(versionResult.Diagnostics);
        }

        try
        {
            TContract? contract = JsonSerializer.Deserialize<TContract>(payload, SerializerOptions);
            return contract is null
                ? CdcContractReadResult<TContract>.Failure([
                    new CdcDiagnostic(
                        CdcDiagnosticCategory.MalformedPayload,
                        "$",
                        "CDC contract payload deserialized to null."
                    ),
                ])
                : CdcContractReadResult<TContract>.Success(contract);
        }
        catch (JsonException exception)
        {
            return CdcContractReadResult<TContract>.Failure([ToDiagnostic(exception)]);
        }
    }

    public static CdcContractValidationResult ValidateRequiredContractVersion(
        JsonElement root,
        int expectedContractVersion = CurrentContractVersion
    )
    {
        CdcDiagnosticCollector diagnostics = new();

        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.MalformedPayload("$", "CDC contract root must be a JSON object.");
            return diagnostics.ToValidationResult();
        }

        if (!root.TryGetProperty(ContractVersionPropertyName, out JsonElement contractVersion))
        {
            diagnostics.MissingRequiredField($"$.{ContractVersionPropertyName}", ContractVersionPropertyName);
            return diagnostics.ToValidationResult();
        }

        if (contractVersion.ValueKind != JsonValueKind.Number || !contractVersion.TryGetInt32(out int value))
        {
            diagnostics.InvalidContractVersion(
                $"$.{ContractVersionPropertyName}",
                "CDC contract version must be an integer."
            );
            return diagnostics.ToValidationResult();
        }

        if (value != expectedContractVersion)
        {
            diagnostics.InvalidContractVersion(
                $"$.{ContractVersionPropertyName}",
                $"CDC contract version `{value}` is not supported. Expected `{expectedContractVersion}`."
            );
        }

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateNotFutureUtcTimestamp(
        DateTimeOffset timestamp,
        DateTimeOffset now,
        string path
    )
    {
        CdcDiagnosticCollector diagnostics = new();

        if (timestamp.Offset != TimeSpan.Zero)
        {
            diagnostics.MalformedPayload(path, "CDC timestamp must use UTC offset `00:00`.");
        }

        if (timestamp > now)
        {
            diagnostics.FutureUtcTimestamp(path, timestamp, now);
        }

        return diagnostics.ToValidationResult();
    }

    private static JsonDocument? TryParsePayload(string payload, out CdcDiagnostic? diagnostic)
    {
        diagnostic = null;

        try
        {
            return JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
        }
        catch (JsonException exception)
        {
            diagnostic = new CdcDiagnostic(
                CdcDiagnosticCategory.MalformedPayload,
                string.IsNullOrWhiteSpace(exception.Path) ? "$" : exception.Path,
                "CDC contract payload is malformed JSON."
            );
            return null;
        }
    }

    private static CdcDiagnostic ToDiagnostic(JsonException exception)
    {
        string path = string.IsNullOrWhiteSpace(exception.Path) ? "$" : exception.Path;

        if (exception is CdcJsonContractException contractException)
        {
            return new CdcDiagnostic(contractException.Category, path, contractException.Message);
        }

        if (exception.Message.Contains("missing required properties", StringComparison.OrdinalIgnoreCase))
        {
            return new CdcDiagnostic(
                CdcDiagnosticCategory.MissingRequiredField,
                path,
                "CDC contract payload is missing required fields."
            );
        }

        return new CdcDiagnostic(
            CdcDiagnosticCategory.MalformedPayload,
            path,
            "CDC contract payload is invalid."
        );
    }
}

public sealed class CdcLowerCamelJsonStringEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type converterType = typeof(CdcLowerCamelJsonStringEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

public sealed class CdcLowerCamelJsonStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<TEnum, string> _namesByValue = CreateNamesByValue();
    private static readonly IReadOnlyDictionary<string, TEnum> _valuesByName = CreateValuesByName();

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new CdcJsonContractException(
                CdcDiagnosticCategory.InvalidEnumValue,
                $"Enum `{typeof(TEnum).Name}` must be encoded as a lower-camel string."
            );
        }

        string? value = reader.GetString();
        if (value is null || !_valuesByName.TryGetValue(value, out TEnum enumValue))
        {
            throw new CdcJsonContractException(
                CdcDiagnosticCategory.InvalidEnumValue,
                $"Enum `{typeof(TEnum).Name}` does not define lower-camel value `{value}`."
            );
        }

        return enumValue;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!_namesByValue.TryGetValue(value, out string? name))
        {
            throw new JsonException($"Enum `{typeof(TEnum).Name}` does not define value `{value}`.");
        }

        writer.WriteStringValue(name);
    }

    private static IReadOnlyDictionary<TEnum, string> CreateNamesByValue() =>
        Enum.GetValues<TEnum>()
            .ToDictionary(value => value, value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));

    private static IReadOnlyDictionary<string, TEnum> CreateValuesByName()
    {
        Dictionary<string, TEnum> valuesByName = [];
        foreach ((TEnum value, string name) in _namesByValue)
        {
            if (!valuesByName.TryAdd(name, value))
            {
                throw new InvalidOperationException(
                    $"Enum `{typeof(TEnum).Name}` has duplicate lower-camel JSON value `{name}`."
                );
            }
        }

        return valuesByName;
    }
}

internal sealed class CdcJsonContractException : JsonException
{
    public CdcJsonContractException(CdcDiagnosticCategory category, string message)
        : base(message)
    {
        Category = category;
    }

    public CdcDiagnosticCategory Category { get; }
}
