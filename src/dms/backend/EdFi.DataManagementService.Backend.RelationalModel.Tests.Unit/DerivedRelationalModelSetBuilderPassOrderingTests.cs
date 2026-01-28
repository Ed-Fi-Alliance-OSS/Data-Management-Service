// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_An_Unordered_Pass_List
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
    public void It_should_execute_passes_in_deterministic_order()
    {
        _executionOrder.Should().Equal(nameof(PassBeta), nameof(PassGamma), nameof(PassAlpha));
    }

    private abstract class RecordingPassBase : IRelationalModelSetPass
    {
        private readonly IList<string> _executionOrder;

        protected RecordingPassBase(IList<string> executionOrder)
        {
            _executionOrder = executionOrder;
        }

        public abstract int Order { get; }

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

        public override int Order { get; } = 30;

        protected override string Label => nameof(PassAlpha);
    }

    private sealed class PassBeta : RecordingPassBase
    {
        public PassBeta(IList<string> executionOrder)
            : base(executionOrder) { }

        public override int Order { get; } = 10;

        protected override string Label => nameof(PassBeta);
    }

    private sealed class PassGamma : RecordingPassBase
    {
        public PassGamma(IList<string> executionOrder)
            : base(executionOrder) { }

        public override int Order { get; } = 20;

        protected override string Label => nameof(PassGamma);
    }
}

[TestFixture]
public class Given_A_Duplicate_Pass_Ordering_Key
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        try
        {
            _ = new DerivedRelationalModelSetBuilder(
                new IRelationalModelSetPass[] { new DuplicateOrderPassAlpha(), new DuplicateOrderPassBeta() }
            );
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_fast_when_pass_ordering_keys_duplicate()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate pass order values");
        _exception.Message.Should().Contain("Order 5");
        _exception.Message.Should().Contain(nameof(DuplicateOrderPassAlpha));
        _exception.Message.Should().Contain(nameof(DuplicateOrderPassBeta));
    }

    private sealed class DuplicateOrderPassAlpha : IRelationalModelSetPass
    {
        public int Order { get; } = 5;

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }
    }

    private sealed class DuplicateOrderPassBeta : IRelationalModelSetPass
    {
        public int Order { get; } = 5;

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }
    }
}
