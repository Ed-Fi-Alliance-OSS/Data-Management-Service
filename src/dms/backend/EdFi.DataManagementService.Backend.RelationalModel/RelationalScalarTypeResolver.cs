// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal static class RelationalScalarTypeResolver
{
    private static readonly IReadOnlySet<string> EmptyStringMaxLengthOmissionPaths = new HashSet<string>(
        StringComparer.Ordinal
    );

    private delegate bool TryGetDecimalPropertyValidationInfo(
        JsonPathExpression path,
        out DecimalPropertyValidationInfo validationInfo
    );

    public static RelationalScalarType ResolveScalarType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        return ResolveScalarType(
            schema,
            sourcePath,
            context.TryGetDecimalPropertyValidationInfo,
            context.StringMaxLengthOmissionPaths
        );
    }

    private static RelationalScalarType ResolveStringType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        IReadOnlySet<string> stringMaxLengthOmissionPaths
    )
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        if (!string.IsNullOrWhiteSpace(format))
        {
            return format switch
            {
                "date" => new RelationalScalarType(ScalarKind.Date),
                "date-time" => new RelationalScalarType(ScalarKind.DateTime),
                "time" => new RelationalScalarType(ScalarKind.Time),
                _ => BuildStringType(schema, sourcePath, stringMaxLengthOmissionPaths),
            };
        }

        return BuildStringType(schema, sourcePath, stringMaxLengthOmissionPaths);
    }

    private static RelationalScalarType BuildStringType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        IReadOnlySet<string> stringMaxLengthOmissionPaths
    )
    {
        if (!schema.TryGetPropertyValue("maxLength", out var maxLengthNode) || maxLengthNode is null)
        {
            if (IsMaxLengthOmissionAllowed(sourcePath, stringMaxLengthOmissionPaths))
            {
                return new RelationalScalarType(ScalarKind.String);
            }

            throw new InvalidOperationException(
                $"String schema maxLength is required at {sourcePath.Canonical}. "
                    + "Set maxLength in MetaEd for string/sharedString."
            );
        }

        if (maxLengthNode is not JsonValue maxLengthValue)
        {
            throw new InvalidOperationException(
                $"Expected maxLength to be a number at {sourcePath.Canonical}."
            );
        }

        var maxLength = maxLengthValue.GetValue<int>();
        if (maxLength <= 0)
        {
            throw new InvalidOperationException(
                $"String schema maxLength must be positive at {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(ScalarKind.String, maxLength);
    }

    private static bool IsMaxLengthOmissionAllowed(
        JsonPathExpression sourcePath,
        IReadOnlySet<string> stringMaxLengthOmissionPaths
    )
    {
        return stringMaxLengthOmissionPaths.Contains(sourcePath.Canonical);
    }

    private static RelationalScalarType ResolveIntegerType(JsonObject schema, JsonPathExpression sourcePath)
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        return format switch
        {
            "int64" => new RelationalScalarType(ScalarKind.Int64),
            _ => new RelationalScalarType(ScalarKind.Int32),
        };
    }

    private static RelationalScalarType ResolveDecimalType(
        JsonPathExpression sourcePath,
        TryGetDecimalPropertyValidationInfo tryGetDecimalPropertyValidationInfo
    )
    {
        if (!tryGetDecimalPropertyValidationInfo(sourcePath, out var validationInfo))
        {
            throw new InvalidOperationException(
                $"Decimal property validation info is required for number properties at {sourcePath.Canonical}."
            );
        }

        if (validationInfo.TotalDigits is null || validationInfo.DecimalPlaces is null)
        {
            throw new InvalidOperationException(
                $"Decimal property validation info must include totalDigits and decimalPlaces at {sourcePath.Canonical}."
            );
        }

        if (validationInfo.TotalDigits <= 0 || validationInfo.DecimalPlaces < 0)
        {
            throw new InvalidOperationException(
                $"Decimal property validation info must be positive for {sourcePath.Canonical}."
            );
        }

        if (validationInfo.DecimalPlaces > validationInfo.TotalDigits)
        {
            throw new InvalidOperationException(
                $"Decimal places cannot exceed total digits for {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(
            ScalarKind.Decimal,
            Decimal: (validationInfo.TotalDigits.Value, validationInfo.DecimalPlaces.Value)
        );
    }

    public static RelationalScalarType ResolveScalarType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        IReadOnlyDictionary<string, DecimalPropertyValidationInfo> decimalPropertyValidationInfosByPath
    )
    {
        ArgumentNullException.ThrowIfNull(decimalPropertyValidationInfosByPath);

        return ResolveScalarType(
            schema,
            sourcePath,
            (JsonPathExpression path, out DecimalPropertyValidationInfo info) =>
                decimalPropertyValidationInfosByPath.TryGetValue(path.Canonical, out info),
            EmptyStringMaxLengthOmissionPaths
        );
    }

    private static RelationalScalarType ResolveScalarType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        TryGetDecimalPropertyValidationInfo tryGetDecimalPropertyValidationInfo,
        IReadOnlySet<string> stringMaxLengthOmissionPaths
    )
    {
        ArgumentNullException.ThrowIfNull(tryGetDecimalPropertyValidationInfo);
        ArgumentNullException.ThrowIfNull(stringMaxLengthOmissionPaths);

        var schemaType = GetSchemaType(schema, sourcePath.Canonical);

        return schemaType switch
        {
            "string" => ResolveStringType(schema, sourcePath, stringMaxLengthOmissionPaths),
            "integer" => ResolveIntegerType(schema, sourcePath),
            "number" => ResolveDecimalType(sourcePath, tryGetDecimalPropertyValidationInfo),
            "boolean" => new RelationalScalarType(ScalarKind.Boolean),
            _ => throw new InvalidOperationException(
                $"Unsupported scalar type '{schemaType}' at {sourcePath.Canonical}."
            ),
        };
    }

    private static string GetSchemaType(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
        {
            throw new InvalidOperationException($"Schema type must be specified at {path}.");
        }

        return typeNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException($"Expected type to be a string at {path}.type."),
        };
    }

    private static string? GetOptionalString(JsonObject schema, string propertyName, string path)
    {
        if (!schema.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        return valueNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string at {path}.{propertyName}."
            ),
        };
    }
}
