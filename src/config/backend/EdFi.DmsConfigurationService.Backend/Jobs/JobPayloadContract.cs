// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The structural contract of a job payload type (spec D-13), checked when a handler is registered so that a
/// type that could carry arbitrary data fails startup instead of reaching the database.
/// </summary>
/// <remarks>
/// A payload type is a sealed, non-generic record or class whose public instance properties are only
/// <c>int</c>, <c>long</c>, <c>short</c>, <c>Guid</c>, <c>bool</c>, an enum, a <c>string</c> annotated with
/// <see cref="JobIdentifierAttribute"/>, an <c>IReadOnlyList&lt;&gt;</c> of one of those, or a nested type
/// that meets the same rules. This is an allowlist: every other type is rejected, and the most likely
/// mistakes (<c>object</c>, JSON DOM types, dictionaries, date and time types, floating point, nullable
/// values, unannotated strings) get a specific message. Public fields, indexers, recursive types,
/// polymorphism, extension data, and custom converters are rejected because each would let a payload carry
/// data the contract does not describe. Nesting is limited to <see cref="MaxDepth"/> JSON levels, counting
/// the root object, each nested object, and each list, so a valid type always serializes within the
/// serializer's depth limit.
/// </remarks>
public static class JobPayloadContract
{
    public const int MaxPayloadLength = 4000;
    public const int MaxIdentifierLength = 256;
    public const int MaxDepth = 8;

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<string>> _violations = new();

    private static readonly HashSet<Type> _scalars =
    [
        typeof(int),
        typeof(long),
        typeof(short),
        typeof(Guid),
        typeof(bool),
    ];

    private static readonly HashSet<Type> _dateAndTimeTypes =
    [
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(TimeSpan),
    ];

    private static readonly HashSet<Type> _floatingPointTypes =
    [
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(Half),
    ];

    /// <summary>The contract violations of <paramref name="payloadType"/>; empty when it is a valid payload type.</summary>
    public static IReadOnlyList<string> Violations(Type payloadType) =>
        _violations.GetOrAdd(
            payloadType,
            type =>
            {
                List<string> violations = [];
                VerifyType(type, type.Name, [], violations, depth: 1);
                return violations;
            }
        );

    /// <summary>Throws when <paramref name="payloadType"/> breaks the contract, naming every violation.</summary>
    public static void EnsureValid(Type payloadType)
    {
        IReadOnlyList<string> violations = Violations(payloadType);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Job payload type '{payloadType.FullName}' breaks the payload contract: {string.Join("; ", violations)}"
            );
        }
    }

    private static void VerifyType(
        Type type,
        string path,
        HashSet<Type> ancestors,
        List<string> violations,
        int depth
    )
    {
        if (depth > MaxDepth)
        {
            violations.Add($"{path}: nests deeper than {MaxDepth} JSON levels");
            return;
        }

        if (
            !type.IsClass
            || !type.IsSealed
            || type.IsAbstract
            || type.IsGenericType
            || type == typeof(string)
        )
        {
            violations.Add($"{path}: '{type.Name}' must be a sealed, non-generic record or class");
            return;
        }

        if (!ancestors.Add(type))
        {
            violations.Add($"{path}: '{type.Name}' contains itself, and a payload type may not be recursive");
            return;
        }

        if (
            type.IsDefined(typeof(JsonPolymorphicAttribute), inherit: false)
            || type.IsDefined(typeof(JsonDerivedTypeAttribute), inherit: false)
        )
        {
            violations.Add($"{path}: '{type.Name}' declares polymorphism, which payloads may not use");
        }

        if (type.IsDefined(typeof(JsonConverterAttribute), inherit: false))
        {
            violations.Add(
                $"{path}: '{type.Name}' declares a custom JSON converter, which payloads may not use"
            );
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            violations.Add($"{path}.{field.Name}: public fields are not allowed; use a property");
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string propertyPath = $"{path}.{property.Name}";

            if (property.GetIndexParameters().Length > 0)
            {
                violations.Add($"{propertyPath}: indexers are not allowed");
                continue;
            }

            if (property.IsDefined(typeof(JsonExtensionDataAttribute), inherit: false))
            {
                violations.Add($"{propertyPath}: extension data is not allowed");
                continue;
            }

            if (property.IsDefined(typeof(JsonConverterAttribute), inherit: false))
            {
                violations.Add($"{propertyPath}: a custom JSON converter is not allowed");
                continue;
            }

            VerifyProperty(property, propertyPath, ancestors, violations, depth);
        }

        ancestors.Remove(type);
    }

    private static void VerifyProperty(
        PropertyInfo property,
        string path,
        HashSet<Type> ancestors,
        List<string> violations,
        int depth
    )
    {
        JobIdentifierAttribute? identifier = property.GetCustomAttribute<JobIdentifierAttribute>();
        Type type = property.PropertyType;
        Type? elementType = ReadOnlyListElementType(type);

        if (identifier is not null)
        {
            if ((elementType ?? type) != typeof(string))
            {
                violations.Add($"{path}: [JobIdentifier] applies only to a string or a list of strings");
                return;
            }

            if (identifier.MaxLength is < 1 or > MaxIdentifierLength)
            {
                violations.Add(
                    $"{path}: [JobIdentifier] maximum length must be between 1 and {MaxIdentifierLength}"
                );
            }
        }

        if (elementType is not null)
        {
            if (ReadOnlyListElementType(elementType) is not null)
            {
                violations.Add($"{path}: a list of lists is not allowed");
                return;
            }

            if (depth + 1 > MaxDepth)
            {
                violations.Add($"{path}: nests deeper than {MaxDepth} JSON levels");
                return;
            }

            VerifyValueType(elementType, identifier, $"{path}[]", ancestors, violations, depth + 1);
            return;
        }

        VerifyValueType(type, identifier, path, ancestors, violations, depth);
    }

    /// <param name="depth">The JSON depth of the object or list that contains the value.</param>
    private static void VerifyValueType(
        Type type,
        JobIdentifierAttribute? identifier,
        string path,
        HashSet<Type> ancestors,
        List<string> violations,
        int depth
    )
    {
        if (type == typeof(string))
        {
            if (identifier is null)
            {
                violations.Add($"{path}: a string must be annotated with [JobIdentifier]");
            }
            return;
        }

        if (_scalars.Contains(type) || type.IsEnum)
        {
            return;
        }

        string? disallowed = DisallowedReason(type);
        if (disallowed is not null)
        {
            violations.Add($"{path}: '{type.Name}' is not allowed ({disallowed})");
            return;
        }

        if (IsFrameworkType(type))
        {
            violations.Add($"{path}: '{type.Name}' is not an allowed payload type");
            return;
        }

        VerifyType(type, path, ancestors, violations, depth + 1);
    }

    private static string? DisallowedReason(Type type)
    {
        if (type == typeof(object))
        {
            return "an untyped value";
        }

        if (
            type == typeof(JsonElement)
            || type == typeof(JsonDocument)
            || typeof(JsonNode).IsAssignableFrom(type)
        )
        {
            return "a JSON document value";
        }

        if (Nullable.GetUnderlyingType(type) is not null)
        {
            return "a nullable value";
        }

        if (_dateAndTimeTypes.Contains(type))
        {
            return "a date or time value";
        }

        if (_floatingPointTypes.Contains(type))
        {
            return "a floating-point value";
        }

        if (IsDictionary(type))
        {
            return "a dictionary";
        }

        if (type.IsArray || (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string)))
        {
            return "a collection other than IReadOnlyList<>";
        }

        return null;
    }

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type)
        || type.GetInterfaces()
            .Append(type)
            .Any(candidate =>
                candidate.IsGenericType
                && (
                    candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                )
            );

    private static bool IsFrameworkType(Type type) =>
        type.Namespace is { } ns
        && (
            ns == "System"
            || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.", StringComparison.Ordinal)
        );

    /// <summary>The element type when <paramref name="type"/> is exactly <c>IReadOnlyList&lt;T&gt;</c>.</summary>
    internal static Type? ReadOnlyListElementType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            ? type.GetGenericArguments()[0]
            : null;
}
