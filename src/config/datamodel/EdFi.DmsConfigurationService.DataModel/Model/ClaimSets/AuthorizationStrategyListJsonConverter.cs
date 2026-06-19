// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class AuthorizationStrategyListJsonConverter : JsonConverter<List<AuthorizationStrategy>>
{
    public override List<AuthorizationStrategy> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var strategies = new List<AuthorizationStrategy>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            string? strategyName = null;

            if (element.TryGetProperty("authStrategyName", out var authStrategyNameProperty))
            {
                strategyName = authStrategyNameProperty.GetString();
            }
            else if (element.TryGetProperty("name", out var nameProperty))
            {
                strategyName = nameProperty.GetString();
            }

            if (string.IsNullOrWhiteSpace(strategyName))
            {
                continue;
            }

            strategies.Add(new AuthorizationStrategy { AuthorizationStrategyName = strategyName });
        }

        return strategies;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<AuthorizationStrategy> value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartArray();

        foreach (var strategy in value)
        {
            writer.WriteStartObject();
            writer.WriteString("authStrategyName", strategy.AuthorizationStrategyName);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
