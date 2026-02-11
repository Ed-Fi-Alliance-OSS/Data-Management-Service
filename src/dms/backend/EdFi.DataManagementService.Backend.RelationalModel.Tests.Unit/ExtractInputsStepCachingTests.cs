// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a relational model build with shared input extraction.
/// </summary>
[TestFixture]
public class Given_A_Relational_Model_Build_With_Shared_Input_Extraction
{
    private int _expectedExtractCount;
    private int _actualExtractCount;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var extractInputsStep = new CountingExtractInputsStep(new ExtractInputsStep());
        var pipeline = new RelationalModelBuilderPipeline(
            new IRelationalModelBuilderStep[]
            {
                extractInputsStep,
                new ValidateJsonSchemaStep(),
                new DiscoverExtensionSitesStep(),
                new DeriveTableScopesAndKeysStep(),
                new DeriveColumnsAndBindDescriptorEdgesStep(),
                new CanonicalizeOrderingStep(),
            }
        );

        IRelationalModelSetPass[] passes =
        [
            new BaseTraversalAndDescriptorBindingPass(pipeline),
            new ExtensionTableDerivationPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new ReferenceBindingPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
        ];

        var context = new RelationalModelSetBuilderContext(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules(),
            extractInputsStep
        );

        foreach (var pass in passes)
        {
            pass.Execute(context);
        }

        _expectedExtractCount = context.EnumerateConcreteResourceSchemasInNameOrder().Count();
        _actualExtractCount = extractInputsStep.CallCount;

        _ = context.BuildResult();
    }

    /// <summary>
    /// It should extract inputs once per resource.
    /// </summary>
    [Test]
    public void It_should_extract_inputs_once_per_resource()
    {
        _actualExtractCount.Should().Be(_expectedExtractCount);
    }

    /// <summary>
    /// Test type counting extract inputs step.
    /// </summary>
    private sealed class CountingExtractInputsStep : IRelationalModelBuilderStep
    {
        private readonly IRelationalModelBuilderStep _inner;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public CountingExtractInputsStep(IRelationalModelBuilderStep inner)
        {
            ArgumentNullException.ThrowIfNull(inner);

            _inner = inner;
        }

        /// <summary>
        /// Gets call count.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelBuilderContext context)
        {
            CallCount++;
            _inner.Execute(context);
        }
    }
}
