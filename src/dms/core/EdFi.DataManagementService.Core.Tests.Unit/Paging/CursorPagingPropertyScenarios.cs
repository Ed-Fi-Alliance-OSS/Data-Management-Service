// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using CsCheck;
using EdFi.DataManagementService.Core.External.Model;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

internal static class CursorPagingPropertyScenarios
{
    internal sealed record Bounds(long Minimum, long Maximum);

    internal static Gen<Bounds> Generate(string shape = "arbitrary") =>
        from first in Gen.Long
        from second in Gen.Long
        select shape switch
        {
            "arbitrary" => new Bounds(first, second),
            "equal" => new Bounds(first, first),
            // Keep these classes intact during shrinking, including when both inputs become equal.
            "inverted" => Inverted(first, second),
            "minimum" => new Bounds(long.MinValue, second),
            "maximum" => new Bounds(first, long.MaxValue),
            "extremes" => new Bounds(long.MaxValue, long.MinValue),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    private static Bounds Inverted(long first, long second)
    {
        var lower = Math.Min(first, second);
        var upper = Math.Max(first, second);
        if (lower < upper)
        {
            return new(upper, lower);
        }
        return lower == long.MinValue ? new(lower + 1, lower) : new(upper, lower - 1);
    }

    internal static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Marker(PageOrderingMode mode) =>
        mode switch
        {
            PageOrderingMode.DocumentId => "d",
            PageOrderingMode.ContentVersion => "c",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    // Independent wire oracle uses standard base64 with explicit URL alphabet conversion.
    internal static string Token(string payload) => Token(Encoding.UTF8.GetBytes(payload));

    internal static string Token(byte[] payload) =>
        Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Token(Bounds bounds, PageOrderingMode mode) =>
        Token($"{Marker(mode)},{Decimal(bounds.Minimum)},{Decimal(bounds.Maximum)}");

    internal static void Check<T>(
        Gen<T> generator,
        Action<T> assertion,
        string distribution,
        int iterations = 100
    )
    {
        generator.Sample(
            assertion,
            iter: iterations,
            threads: 1,
            print: value => JsonSerializer.Serialize(value)
        );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: {distribution}: {iterations} samples");
    }
}
