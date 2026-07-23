// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata describing the syntactically-typed (integer) query parameters of a list endpoint's
/// <c>[AsParameters]</c> query DTO. Attached once at endpoint construction; read by the global exception
/// handler (via <c>IExceptionHandlerFeature.Endpoint</c>) to classify a query-binding failure
/// (e.g. offset=abc) as urn:ed-fi:api:bad-request:parameter without inspecting the framework exception
/// message. Only integer parameters are represented because string query parameters never fail binding.
///
/// The parser reproduces minimal-API scalar binding: nullable int/long, invariant culture, leading sign
/// and surrounding whitespace allowed. Repeated-value extraction and case-insensitive name lookup are the
/// consuming classifier's responsibility (confirmed against the real host); this type defines the
/// per-value parser and a deterministic parameter order (offset, limit, then remaining wire names
/// ordinal-ignore-case), never relying on reflection enumeration order.
/// </summary>
public sealed class ParameterValidationMetadata
{
    public ImmutableArray<QueryParameter> Parameters { get; }

    private ParameterValidationMetadata(ImmutableArray<QueryParameter> parameters) => Parameters = parameters;

    /// <summary>A single integer query parameter: its wire name and an invariant "is this value bindable" test.</summary>
    public sealed record QueryParameter(string WireName, Func<string, bool> IsBindable);

    /// <summary>
    /// Builds the metadata for an <c>[AsParameters]</c> query DTO type by reflecting its <c>[FromQuery]</c>
    /// integer properties across the full type hierarchy, de-duplicating hidden/inherited ('new') members
    /// by preferring the most-derived declaration per wire name.
    /// </summary>
    public static ParameterValidationMetadata ForQueryType(Type dtoType)
    {
        ArgumentNullException.ThrowIfNull(dtoType);

        var candidates = dtoType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => new
            {
                Property = property,
                FromQuery = property.GetCustomAttribute<FromQueryAttribute>(inherit: true),
                Underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
            })
            .Where(x =>
                x.FromQuery is not null && (x.Underlying == typeof(int) || x.Underlying == typeof(long))
            )
            .Select(x => new
            {
                WireName = string.IsNullOrEmpty(x.FromQuery!.Name) ? x.Property.Name : x.FromQuery.Name,
                x.Underlying,
                DeclaringDepth = Depth(x.Property.DeclaringType),
            });

        var parameters = candidates
            // De-dup hidden/inherited members: one entry per wire name, most-derived declaration wins.
            .GroupBy(x => x.WireName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.DeclaringDepth).First())
            .OrderBy(x => OrderKey(x.WireName))
            .ThenBy(x => x.WireName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new QueryParameter(x.WireName, ParserFor(x.Underlying)))
            .ToImmutableArray();

        return new ParameterValidationMetadata(parameters);
    }

    // offset first, limit second, then remaining integer parameters by wire name (ordinal-ignore-case).
    private static int OrderKey(string wireName)
    {
        if (wireName.Equals("offset", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (wireName.Equals("limit", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static int Depth(Type? type)
    {
        int depth = 0;
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            depth++;
        }
        return depth;
    }

    // Matches minimal-API IParsable<int>/<long> binding: NumberStyles.Integer (leading sign + surrounding
    // whitespace) with the invariant culture.
    private static Func<string, bool> ParserFor(Type underlying) =>
        underlying == typeof(int)
            ? value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            : value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}
