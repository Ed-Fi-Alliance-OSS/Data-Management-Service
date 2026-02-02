// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Pass_List_With_Custom_Order
{
    private IReadOnlyList<string> _executionOrder = default!;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        List<string> executionOrder = [];

        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new PassGamma(executionOrder),
                new PassBeta(executionOrder),
                new PassAlpha(executionOrder),
            }
        );

        builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _executionOrder = executionOrder.ToArray();
    }

    [Test]
    public void It_should_execute_passes_in_supplied_order()
    {
        _executionOrder.Should().Equal(nameof(PassGamma), nameof(PassBeta), nameof(PassAlpha));
    }

    private abstract class RecordingPassBase : IRelationalModelSetPass
    {
        private readonly IList<string> _executionOrder;

        protected RecordingPassBase(IList<string> executionOrder)
        {
            _executionOrder = executionOrder;
        }

        protected abstract string Label { get; }

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            _executionOrder.Add(Label);
        }
    }

    private sealed class PassAlpha : RecordingPassBase
    {
        public PassAlpha(IList<string> executionOrder)
            : base(executionOrder) { }

        protected override string Label => nameof(PassAlpha);
    }

    private sealed class PassBeta : RecordingPassBase
    {
        public PassBeta(IList<string> executionOrder)
            : base(executionOrder) { }

        protected override string Label => nameof(PassBeta);
    }

    private sealed class PassGamma : RecordingPassBase
    {
        public PassGamma(IList<string> executionOrder)
            : base(executionOrder) { }

        protected override string Label => nameof(PassGamma);
    }
}
