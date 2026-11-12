// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CsCheck;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

internal static class NumericIdentityPropertyScenarios
{
    internal sealed record DecimalScenario(
        int Low,
        int Middle,
        int High,
        byte Scale,
        bool Negative,
        int Zeros
    )
    {
        internal decimal Value => new(Low, Middle, High, Negative, Scale);

        // Build an unsigned 96-bit coefficient independently of decimal parsing/formatting.
        internal BigInteger Coefficient =>
            (BigInteger)(uint)Low + ((BigInteger)(uint)Middle << 32) + ((BigInteger)(uint)High << 64);

        internal string Digits => Coefficient.ToString(CultureInfo.InvariantCulture);

        internal string Sign => Negative ? "-" : "+";

        internal string FixedPoint
        {
            get
            {
                var digits = Digits.PadLeft(Scale + 1, '0');
                return Scale == 0 ? digits : digits.Insert(digits.Length - Scale, ".");
            }
        }

        // Oracle uses only coefficient digits and decimal-point placement, never decimal.ToString.
        internal string Expected
        {
            get
            {
                if (Coefficient.IsZero)
                {
                    return "0";
                }
                var magnitude = Scale == 0 ? FixedPoint : FixedPoint.TrimEnd('0').TrimEnd('.');
                return Negative ? "-" + magnitude : magnitude;
            }
        }

        internal string[] Spellings() =>
            [
                (Negative ? "-" : "") + FixedPoint,
                Sign + "000" + FixedPoint + (Scale == 0 ? "." : "") + new string('0', Zeros),
                Sign + Digits + "e-" + Scale.ToString(CultureInfo.InvariantCulture),
                Sign
                    + Digits
                    + new string('0', Zeros)
                    + "E-"
                    + (Scale + Zeros).ToString(CultureInfo.InvariantCulture),
                Sign
                    + "0."
                    + Digits
                    + "E"
                    + (Digits.Length - Scale).ToString("+0;-0;0", CultureInfo.InvariantCulture),
            ];
    }

    internal static Gen<DecimalScenario> Generate(string shape, bool negative) =>
        from low in Gen.Int
        from middle in Gen.Int
        from high in Gen.Int
        from scale in Gen.Int[1, 28]
        from zeros in Gen.Int[1, 6]
        from trailingCoefficient in Gen.Int[1, 1000000]
        select shape switch
        {
            "integer" => new DecimalScenario(low, middle, high, 0, negative, zeros),
            "fractional" => new DecimalScenario(low, middle, high, (byte)scale, negative, zeros),
            "scale28" => new DecimalScenario(low, middle, high, 28, negative, zeros),
            "maximum" => new DecimalScenario(-1, -1, -1, 0, negative, zeros),
            "minimumUnit" => new DecimalScenario(1, 0, 0, 28, negative, zeros),
            "zero" => new DecimalScenario(0, 0, 0, (byte)scale, negative, zeros),
            "trailingZero" => new DecimalScenario(
                trailingCoefficient * 10,
                0,
                0,
                (byte)scale,
                negative,
                zeros
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    internal static void Check(
        Gen<DecimalScenario> generator,
        Action<DecimalScenario> assertion,
        string distribution
    )
    {
        generator.Sample(assertion, iter: 100, threads: 1, print: value => JsonSerializer.Serialize(value));
        TestContext.Out.WriteLine($"CsCheck 5.0.1: {distribution}: 100 samples, no discards");
    }
}
