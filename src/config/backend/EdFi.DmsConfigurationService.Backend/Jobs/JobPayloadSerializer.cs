// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>The fixed reason codes a payload is rejected with (spec D-13). They never carry input text.</summary>
public static class JobPayloadFailureReasons
{
    /// <summary>More than <see cref="JobPayloadContract.MaxPayloadLength"/> UTF-16 code units.</summary>
    public const string PayloadTooLarge = nameof(PayloadTooLarge);

    /// <summary>A high surrogate without its low surrogate, or a low surrogate on its own.</summary>
    public const string UnpairedSurrogate = nameof(UnpairedSurrogate);

    /// <summary>The first character after JSON whitespace is not <c>{</c>.</summary>
    public const string NotAnObject = nameof(NotAnObject);

    /// <summary>
    /// Not valid for the payload type: malformed JSON, an unexpected or duplicate member, <c>$type</c> or
    /// other metadata, a trailing comma, a comment, a number in a string, or nesting deeper than
    /// <see cref="JobPayloadContract.MaxDepth"/>.
    /// </summary>
    public const string InvalidJson = nameof(InvalidJson);

    /// <summary>A string, list, list element, or nested object is missing or null.</summary>
    public const string MissingValue = nameof(MissingValue);

    /// <summary>An identifier does not match its pattern or is longer than its declared maximum.</summary>
    public const string InvalidIdentifier = nameof(InvalidIdentifier);

    /// <summary>An enum member holds a value the enum does not define.</summary>
    public const string UndefinedEnumValue = nameof(UndefinedEnumValue);
}

public record JobPayloadWriteResult
{
    public record Success(string Json) : JobPayloadWriteResult;

    public record Failure(string ReasonCode) : JobPayloadWriteResult;
}

public record JobPayloadReadResult<TPayload>
    where TPayload : class
{
    public record Success(TPayload Payload) : JobPayloadReadResult<TPayload>;

    public record Failure(string ReasonCode) : JobPayloadReadResult<TPayload>;
}

/// <summary>
/// Serializes and deserializes job payloads under the strict rules of spec D-13. Both directions check the
/// payload type against <see cref="JobPayloadContract"/> and throw when it is invalid, because that is a
/// programming error; a payload value that breaks the rules is a <c>Failure</c> with a fixed reason code.
/// </summary>
/// <remarks>
/// The options start from <see cref="JsonSerializerDefaults.Web"/> (camelCase names) and remove its lenient
/// settings: property names are matched case-sensitively, numbers are not read from strings, duplicate
/// properties are rejected, and constructor parameters and nullable annotations are enforced. Unmapped
/// members are rejected, which also rejects <c>$type</c>, because no polymorphism is configured.
/// </remarks>
public static class JobPayloadSerializer
{
    /// <summary>
    /// The fixed options. <see cref="JobPayloadContract"/> checks payload types against the contract these
    /// options resolve.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = JobPayloadContract.MaxDepth,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static JobPayloadWriteResult Serialize<TPayload>(TPayload payload)
        where TPayload : class
    {
        JobPayloadContract.EnsureValid(typeof(TPayload));

        if (ValueViolation(payload, typeof(TPayload)) is { } violation)
        {
            return new JobPayloadWriteResult.Failure(violation);
        }

        string json = JsonSerializer.Serialize(payload, Options);

        // The written text must pass the same checks as text that is read: size, surrogate pairing, and an
        // object root.
        return TextViolation(json) is { } textViolation
            ? new JobPayloadWriteResult.Failure(textViolation)
            : new JobPayloadWriteResult.Success(json);
    }

    public static JobPayloadReadResult<TPayload> Deserialize<TPayload>(string json)
        where TPayload : class
    {
        JobPayloadContract.EnsureValid(typeof(TPayload));

        if (TextViolation(json) is { } textViolation)
        {
            return new JobPayloadReadResult<TPayload>.Failure(textViolation);
        }

        TPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TPayload>(json, Options);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Neither the exception message nor the input is kept: both can echo payload content.
            return new JobPayloadReadResult<TPayload>.Failure(JobPayloadFailureReasons.InvalidJson);
        }

        if (ValueViolation(payload, typeof(TPayload)) is { } valueViolation)
        {
            return new JobPayloadReadResult<TPayload>.Failure(valueViolation);
        }

        return new JobPayloadReadResult<TPayload>.Success(payload!);
    }

    /// <summary>
    /// The text checks that need no payload type: size in UTF-16 code units, surrogate pairing, and an object
    /// root after any JSON whitespace (space, tab, line feed, carriage return), the same prefix rule as the
    /// database's <c>CK_*_Payload_Object</c> constraints.
    /// </summary>
    public static string? TextViolation(string json)
    {
        if (json.Length > JobPayloadContract.MaxPayloadLength)
        {
            return JobPayloadFailureReasons.PayloadTooLarge;
        }

        int index = 0;
        while (index < json.Length)
        {
            if (char.IsHighSurrogate(json[index]))
            {
                if (index + 1 == json.Length || !char.IsLowSurrogate(json[index + 1]))
                {
                    return JobPayloadFailureReasons.UnpairedSurrogate;
                }
                index += 2;
            }
            else if (char.IsLowSurrogate(json[index]))
            {
                return JobPayloadFailureReasons.UnpairedSurrogate;
            }
            else
            {
                index++;
            }
        }

        int first = 0;
        while (first < json.Length && json[first] is ' ' or '\t' or '\n' or '\r')
        {
            first++;
        }

        return first < json.Length && json[first] == '{' ? null : JobPayloadFailureReasons.NotAnObject;
    }

    /// <summary>
    /// Walks a value of a contract-verified type and returns the first rule it breaks: a null string, list,
    /// element, or nested object; an identifier outside its pattern or length; an undefined enum value.
    /// </summary>
    private static string? ValueViolation(object? value, Type type)
    {
        if (value is null)
        {
            return JobPayloadFailureReasons.MissingValue;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            JobIdentifierAttribute? identifier = property.GetCustomAttribute<JobIdentifierAttribute>();
            object? propertyValue = property.GetValue(value);
            Type? elementType = JobPayloadContract.ReadOnlyListElementType(property.PropertyType);

            string? violation;
            if (elementType is null)
            {
                violation = MemberViolation(propertyValue, property.PropertyType, identifier);
            }
            else if (propertyValue is null)
            {
                violation = JobPayloadFailureReasons.MissingValue;
            }
            else
            {
                violation = ((IEnumerable)propertyValue)
                    .Cast<object?>()
                    .Select(element => MemberViolation(element, elementType, identifier))
                    .FirstOrDefault(elementViolation => elementViolation is not null);
            }

            if (violation is not null)
            {
                return violation;
            }
        }

        return null;
    }

    private static string? MemberViolation(object? value, Type type, JobIdentifierAttribute? identifier)
    {
        if (type == typeof(string))
        {
            return value switch
            {
                null => JobPayloadFailureReasons.MissingValue,
                string text when identifier!.Accepts(text) => null,
                _ => JobPayloadFailureReasons.InvalidIdentifier,
            };
        }

        if (type.IsEnum)
        {
            return Enum.IsDefined(type, value!) ? null : JobPayloadFailureReasons.UndefinedEnumValue;
        }

        return type.IsValueType ? null : ValueViolation(value, type);
    }
}
