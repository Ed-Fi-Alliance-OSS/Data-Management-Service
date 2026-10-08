// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

public sealed record MessageContractFixture(
    string ScenarioId,
    string MaterializedCase,
    JsonElement CacheRow,
    JsonElement ExpectedEnvelope,
    JsonElement SourceRecord
);

/// <summary>Loads shared E18 expectations and expands language-neutral Connect input templates.</summary>
public static class MessageContractFixtureCatalog
{
    private const string CatalogPath = "cdc/message-contract/catalog.json";

    public static IReadOnlyList<MessageContractFixture> LoadAll(string startDirectory)
    {
        string root = ResolveFixtureRoot(startDirectory);
        return Guard(() =>
        {
            JsonElement catalog = Read(Path.Combine(root, CatalogPath));
            Require(catalog.GetProperty("formatVersion").GetInt32() == 1, "catalog-version");
            MessageContractFixture[] fixtures = catalog
                .GetProperty("scenarios")
                .EnumerateArray()
                .Select(entry => Load(root, entry))
                .ToArray();
            Require(fixtures.Length > 0, "empty-catalog");
            Require(
                fixtures.Select(f => f.ScenarioId).Distinct(StringComparer.Ordinal).Count()
                    == fixtures.Length,
                "duplicate-scenario"
            );
            return fixtures;
        });
    }

    public static string ResolveFixtureRoot(string startDirectory)
    {
        string directory = Path.GetFullPath(startDirectory);
        string[] relativeRoots = ["Fixtures", "src/dms/backend/Fixtures", ""];
        while (true)
        {
            foreach (string relative in relativeRoots)
            {
                string candidate = Path.Combine(directory, relative);
                if (File.Exists(Path.Combine(candidate, CatalogPath)))
                {
                    return candidate;
                }
            }

            DirectoryInfo parent = Directory.GetParent(directory) ?? throw Failure("fixture-root-missing");
            directory = parent.FullName;
        }
    }

    private static MessageContractFixture Load(string root, JsonElement entry)
    {
        string scenarioId = Text(entry, "scenarioId");
        Require(
            scenarioId.Length <= 160
                && scenarioId.StartsWith("MC-", StringComparison.Ordinal)
                && scenarioId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'),
            "scenario-id"
        );
        string caseIdentity = Text(entry, "materializedCase");
        Require(
            caseIdentity.StartsWith("document-cache/materialized-documents/", StringComparison.Ordinal),
            "materialized-case"
        );
        string caseDirectory = ResolveRelative(root, caseIdentity);
        JsonElement cacheRow = Read(Path.Combine(caseDirectory, "expected-cache-row.json"));
        JsonElement etag = Read(Path.Combine(caseDirectory, "expected-stream-etag.json"));
        JsonElement publicBody = Read(Path.Combine(caseDirectory, "expected-public-cdc-document.json"));
        // Always validate the authoritative shared files before deriving a synthetic variant.
        JsonElement envelope = BuildExpectedEnvelope(cacheRow, etag, publicBody);
        if (entry.TryGetProperty("upsertVariant", out _))
        {
            JsonElement variant = Read(
                ResolveRelative(Path.Combine(root, "cdc/message-contract"), Text(entry, "upsertVariant"))
            );
            (cacheRow, publicBody) = ApplyUpsertVariant(cacheRow, publicBody, variant);
            envelope = BuildExpectedEnvelope(cacheRow, etag, publicBody);
        }
        string providerPath = ResolveRelative(
            Path.Combine(root, "cdc/message-contract"),
            Text(entry, "providerInput")
        );
        JsonElement template = Read(providerPath);
        JsonNode expanded = Expand(template, cacheRow, template.GetProperty("schemas"), 0);
        expanded.AsObject().Remove("schemas");
        JsonElement sourceRecord = JsonSerializer.SerializeToElement(expanded);
        ValidateSourceRecord(sourceRecord);
        return new(scenarioId, caseIdentity, cacheRow, envelope, sourceRecord);
    }

    private static (JsonElement CacheRow, JsonElement PublicBody) ApplyUpsertVariant(
        JsonElement cacheRow,
        JsonElement publicBody,
        JsonElement variant
    )
    {
        Require(variant.GetProperty("formatVersion").GetInt32() == 1, "variant-version");
        Require(
            variant
                .EnumerateObject()
                .All(p =>
                    p.Name
                        is "formatVersion"
                            or "contentVersion"
                            or "lastModifiedAt"
                            or "expectedLastModifiedAt"
                            or "documentProperties"
                ),
            "variant-field"
        );
        JsonNode row = JsonNode.Parse(cacheRow.GetRawText())!;
        JsonNode body = JsonNode.Parse(publicBody.GetRawText())!;
        if (variant.TryGetProperty("contentVersion", out JsonElement version))
        {
            Require(version.TryGetInt64(out long contentVersion), "variant-content-version");
            row["contentVersion"] = contentVersion;
        }
        if (variant.TryGetProperty("lastModifiedAt", out _))
        {
            row["lastModifiedAt"] = Text(variant, "lastModifiedAt");
            // The expected second is explicit input, independent of the transform's truncation.
            string expectedTime = Text(variant, "expectedLastModifiedAt");
            row["documentJson"]!["_lastModifiedDate"] = expectedTime;
            body["document"]!["_lastModifiedDate"] = expectedTime;
        }
        else
        {
            Require(!variant.TryGetProperty("expectedLastModifiedAt", out _), "variant-timestamp-pair");
        }
        if (variant.TryGetProperty("documentProperties", out JsonElement properties))
        {
            Require(properties.ValueKind == JsonValueKind.Object, "variant-document-properties");
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                // Variants add focused data; they cannot replace shared fields or reserved metadata.
                Require(
                    !property.Name.StartsWith('_')
                        && !body["document"]!.AsObject().ContainsKey(property.Name),
                    "variant-document-property-conflict"
                );
                row["documentJson"]![property.Name] = JsonNode.Parse(property.Value.GetRawText());
                body["document"]![property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
        return (JsonSerializer.SerializeToElement(row), JsonSerializer.SerializeToElement(body));
    }

    public static JsonElement BuildExpectedEnvelope(
        JsonElement cacheRow,
        JsonElement etag,
        JsonElement publicBody
    ) =>
        Guard(() =>
        {
            Require(
                cacheRow.GetProperty("documentId").TryGetInt64(out long documentId) && documentId > 0,
                "cache-document-id"
            );
            Require(
                cacheRow.GetProperty("contentVersion").TryGetInt64(out long contentVersion),
                "cache-content-version"
            );
            string uuid = Text(cacheRow, "documentUuid");
            Require(Guid.TryParseExact(uuid, "D", out Guid parsedUuid), "cache-uuid");
            uuid = parsedUuid.ToString("D");
            string rawTimestamp = Text(cacheRow, "lastModifiedAt");
            Require(
                DateTimeOffset.TryParse(
                    rawTimestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset timestamp
                )
                    && timestamp.Offset == TimeSpan.Zero
                    && rawTimestamp.EndsWith('Z'),
                "cache-timestamp"
            );
            string lastModifiedAt = timestamp.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture
            );
            string streamEtag = Text(etag, "streamEtag");
            Require(Text(cacheRow, "streamEtag") == streamEtag, "cache-etag-mismatch");
            JsonElement body = publicBody.GetProperty("document");
            Require(
                body.ValueKind == JsonValueKind.Object
                    && Text(body, "id") == uuid
                    && Text(body, "_lastModifiedDate") == lastModifiedAt
                    && Text(body, "_etag") == streamEtag,
                "public-body-metadata"
            );
            JsonElement cachedDocument = cacheRow.GetProperty("documentJson");
            Require(
                cachedDocument.ValueKind == JsonValueKind.Object
                    && !cachedDocument.TryGetProperty("_etag", out _),
                "cache-document-shape"
            );
            JsonObject cachedWithEtag = JsonNode.Parse(cachedDocument.GetRawText())!.AsObject();
            cachedWithEtag["_etag"] = streamEtag;
            Require(
                MessageContractJson.Equivalent(JsonSerializer.SerializeToElement(cachedWithEtag), body),
                "public-body-mismatch"
            );
            return JsonSerializer.SerializeToElement(
                new
                {
                    contractVersion = 1,
                    documentUuid = uuid,
                    projectName = Text(cacheRow, "projectName"),
                    resourceName = Text(cacheRow, "resourceName"),
                    resourceVersion = Text(cacheRow, "resourceVersion"),
                    contentVersion,
                    lastModifiedAt,
                    document = body,
                }
            );
        });

    public static void ValidateSourceRecord(JsonElement record) =>
        Guard(() =>
        {
            Require(record.GetProperty("formatVersion").GetInt32() == 1, "input-version");
            Require(Text(record, "provider") is "postgresql" or "sqlserver", "provider");
            _ = Text(record, "sourceTopic");
            _ = Text(record, "unavailableValuePlaceholder");
            Require(
                record.GetProperty("sourcePartition").ValueKind == JsonValueKind.Object,
                "source-partition"
            );
            Require(record.GetProperty("sourceOffset").ValueKind == JsonValueKind.Object, "source-offset");
            Require(
                Text(record, "operation") is "c" or "u" or "r" or "d" or "t" or "native-heartbeat",
                "operation"
            );
            JsonElement timestamp = record.GetProperty("timestamp");
            Require(
                timestamp.ValueKind == JsonValueKind.Null || timestamp.TryGetInt64(out _),
                "record-timestamp"
            );
            ValidateSchemaValue(record.GetProperty("keySchema"), record.GetProperty("key"));
            ValidateSchemaValue(record.GetProperty("valueSchema"), record.GetProperty("value"));
            foreach (JsonElement header in record.GetProperty("headers").EnumerateArray())
            {
                _ = Text(header, "key");
                ValidateSchemaValue(header.GetProperty("schema"), header.GetProperty("value"));
            }
            return true;
        });

    public static void ValidateSchemaValue(JsonElement schema, JsonElement value) =>
        Guard(() =>
        {
            ValidateSchema(schema, 0);
            ValidateValue(schema, value, 0);
            return true;
        });

    private static void ValidateSchema(JsonElement schema, int depth)
    {
        Require(depth <= 32 && schema.ValueKind == JsonValueKind.Object, "schema-shape");
        string type = Text(schema, "type");
        Require(
            schema.GetProperty("optional").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "schema-optional"
        );
        if (schema.TryGetProperty("name", out _))
        {
            _ = Text(schema, "name");
        }
        if (schema.TryGetProperty("version", out JsonElement version))
        {
            Require(version.TryGetInt32(out int number) && number >= 0, "schema-version");
        }
        Require(
            type
                is "STRING"
                    or "INT8"
                    or "INT16"
                    or "INT32"
                    or "INT64"
                    or "FLOAT32"
                    or "FLOAT64"
                    or "BOOLEAN"
                    or "BYTES"
                    or "STRUCT"
                    or "ARRAY",
            "schema-type"
        );
        if (type == "STRUCT")
        {
            JsonElement fields = schema.GetProperty("fields");
            Require(fields.ValueKind == JsonValueKind.Object, "schema-fields");
            Require(
                fields.EnumerateObject().Select(f => f.Name).Distinct(StringComparer.Ordinal).Count()
                    == fields.EnumerateObject().Count(),
                "duplicate-schema-field"
            );
            foreach (JsonProperty field in fields.EnumerateObject())
            {
                Require(!string.IsNullOrWhiteSpace(field.Name), "schema-field-name");
                ValidateSchema(field.Value, depth + 1);
            }
        }
        if (type == "ARRAY")
        {
            ValidateSchema(schema.GetProperty("items"), depth + 1);
        }
    }

    private static void ValidateValue(JsonElement schema, JsonElement value, int depth)
    {
        Require(depth <= 32, "value-depth");
        if (value.ValueKind == JsonValueKind.Null)
        {
            Require(schema.GetProperty("optional").GetBoolean(), "required-value-null");
            return;
        }
        string type = Text(schema, "type");
        bool valid = type switch
        {
            "STRING" => value.ValueKind == JsonValueKind.String,
            "BOOLEAN" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "INT8" => value.ValueKind == JsonValueKind.Number && value.TryGetSByte(out _),
            "INT16" => value.ValueKind == JsonValueKind.Number && value.TryGetInt16(out _),
            "INT32" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "INT64" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "FLOAT32" => value.ValueKind == JsonValueKind.Number
                && value.TryGetSingle(out float f)
                && float.IsFinite(f),
            "FLOAT64" => value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double d)
                && double.IsFinite(d),
            "BYTES" => value.ValueKind == JsonValueKind.String && value.TryGetBytesFromBase64(out _),
            "STRUCT" => value.ValueKind == JsonValueKind.Object,
            "ARRAY" => value.ValueKind == JsonValueKind.Array,
            _ => false,
        };
        Require(valid, "schema-value-type");
        if (type == "STRUCT")
        {
            JsonElement fields = schema.GetProperty("fields");
            Require(
                value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    == value.EnumerateObject().Count(),
                "duplicate-value-field"
            );
            foreach (JsonProperty field in fields.EnumerateObject())
            {
                if (value.TryGetProperty(field.Name, out JsonElement fieldValue))
                {
                    ValidateValue(field.Value, fieldValue, depth + 1);
                }
                else
                {
                    Require(field.Value.GetProperty("optional").GetBoolean(), "required-field-missing");
                }
            }
            Require(
                value.EnumerateObject().All(p => fields.TryGetProperty(p.Name, out _)),
                "unknown-value-field"
            );
        }
        if (type == "ARRAY")
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                ValidateValue(schema.GetProperty("items"), item, depth + 1);
            }
        }
    }

    private static JsonNode Expand(JsonElement node, JsonElement cacheRow, JsonElement schemas, int depth)
    {
        Require(depth <= 32, "template-depth");
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("$cacheRow", out JsonElement binding))
            {
                JsonElement value = cacheRow.GetProperty(binding.GetString()!);
                if (node.TryGetProperty("encoding", out JsonElement encoding))
                {
                    Require(encoding.GetString() == "json-string", "binding-encoding");
                    return JsonValue.Create(value.GetRawText());
                }
                return JsonNode.Parse(value.GetRawText())!;
            }
            if (node.TryGetProperty("$schema", out JsonElement reference))
            {
                return Expand(schemas.GetProperty(reference.GetString()!), cacheRow, schemas, depth + 1);
            }
            JsonObject result = new();
            foreach (JsonProperty property in node.EnumerateObject())
            {
                result.Add(
                    property.Name,
                    property.Value.ValueKind == JsonValueKind.Null
                        ? null
                        : Expand(property.Value, cacheRow, schemas, depth + 1)
                );
            }
            return result;
        }
        if (node.ValueKind == JsonValueKind.Array)
        {
            JsonArray result = new();
            foreach (JsonElement item in node.EnumerateArray())
            {
                result.Add(
                    item.ValueKind == JsonValueKind.Null ? null : Expand(item, cacheRow, schemas, depth + 1)
                );
            }
            return result;
        }
        return JsonNode.Parse(node.GetRawText())!;
    }

    private static JsonElement Read(string path)
    {
        Require(File.Exists(path), "shared-or-input-file-missing");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string ResolveRelative(string root, string relative)
    {
        Require(
            !Path.IsPathRooted(relative)
                && !relative.Contains('\\')
                && Array.TrueForAll(relative.Split('/'), p => p is not ".." and not "." and not ""),
            "fixture-relative-path"
        );
        return Path.Combine(root, relative);
    }

    private static string Text(JsonElement node, string property)
    {
        JsonElement value = node.GetProperty(property);
        Require(
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
            "required-string"
        );
        return value.GetString()!;
    }

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
                        or OverflowException
                        or IOException
                        or ArgumentException
            )
        {
            // Do not forward parser exceptions, file paths, property names or record contents.
            throw Failure("invalid-or-incomplete-fixture");
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw Failure(reason);
        }
    }

    private static MessageContractFixtureException Failure(string reason) => new(reason);
}

public sealed class MessageContractFixtureException(string reason)
    : Exception($"CDC message-contract fixture: {reason}. Payloads redacted.");
