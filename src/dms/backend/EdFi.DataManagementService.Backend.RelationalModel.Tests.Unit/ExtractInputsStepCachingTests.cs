// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Relational_Model_Build_With_Shared_Input_Extraction
{
    private int _expectedExtractCount;
    private int _actualExtractCount;

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
            new BaseTraversalAndDescriptorBindingRelationalModelSetPass(pipeline),
            new ExtensionTableDerivationRelationalModelSetPass(),
            new AbstractIdentityTableDerivationRelationalModelSetPass(),
            new ReferenceBindingRelationalModelSetPass(),
            new RootIdentityConstraintRelationalModelSetPass(),
            new ReferenceConstraintRelationalModelSetPass(),
            new ArrayUniquenessConstraintRelationalModelSetPass(),
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

    [Test]
    public void It_should_extract_inputs_once_per_resource()
    {
        _actualExtractCount.Should().Be(_expectedExtractCount);
    }

    private sealed class CountingExtractInputsStep : IRelationalModelBuilderStep
    {
        private readonly IRelationalModelBuilderStep _inner;

        public CountingExtractInputsStep(IRelationalModelBuilderStep inner)
        {
            ArgumentNullException.ThrowIfNull(inner);

            _inner = inner;
        }

        public int CallCount { get; private set; }

        public void Execute(RelationalModelBuilderContext context)
        {
            CallCount++;
            _inner.Execute(context);
        }
    }
}
