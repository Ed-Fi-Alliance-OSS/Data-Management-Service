// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a pass list with custom order.
/// </summary>
[TestFixture]
public class Given_A_Pass_List_With_Custom_Order
{
    private IReadOnlyList<string> _executionOrder = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should execute passes in supplied order.
    /// </summary>
    [Test]
    public void It_should_execute_passes_in_supplied_order()
    {
        _executionOrder.Should().Equal(nameof(PassGamma), nameof(PassBeta), nameof(PassAlpha));
    }

    /// <summary>
    /// Test type recording pass base.
    /// </summary>
    private abstract class RecordingPassBase : IRelationalModelSetPass
    {
        private readonly IList<string> _executionOrder;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        protected RecordingPassBase(IList<string> executionOrder)
        {
            _executionOrder = executionOrder;
        }

        /// <summary>
        /// Gets label.
        /// </summary>
        protected abstract string Label { get; }

        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            _executionOrder.Add(Label);
        }
    }

    /// <summary>
    /// Test type pass alpha.
    /// </summary>
    private sealed class PassAlpha : RecordingPassBase
    {
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public PassAlpha(IList<string> executionOrder)
            : base(executionOrder) { }

        /// <summary>
        /// Gets label.
        /// </summary>
        protected override string Label => nameof(PassAlpha);
    }

    /// <summary>
    /// Test type pass beta.
    /// </summary>
    private sealed class PassBeta : RecordingPassBase
    {
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public PassBeta(IList<string> executionOrder)
            : base(executionOrder) { }

        /// <summary>
        /// Gets label.
        /// </summary>
        protected override string Label => nameof(PassBeta);
    }

    /// <summary>
    /// Test type pass gamma.
    /// </summary>
    private sealed class PassGamma : RecordingPassBase
    {
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public PassGamma(IList<string> executionOrder)
            : base(executionOrder) { }

        /// <summary>
        /// Gets label.
        /// </summary>
        protected override string Label => nameof(PassGamma);
    }
}
