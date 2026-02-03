// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Default_RelationalModelSet_Passes
{
    private IReadOnlyList<Type> _firstPassTypes = default!;
    private IReadOnlyList<Type> _secondPassTypes = default!;

    [SetUp]
    public void Setup()
    {
        _firstPassTypes = RelationalModelSetPasses.CreateDefault().Select(pass => pass.GetType()).ToArray();
        _secondPassTypes = RelationalModelSetPasses.CreateDefault().Select(pass => pass.GetType()).ToArray();
    }

    [Test]
    public void It_should_expose_the_canonical_pass_order()
    {
        _firstPassTypes
            .Should()
            .Equal(
                typeof(BaseTraversalAndDescriptorBindingRelationalModelSetPass),
                typeof(ExtensionTableDerivationRelationalModelSetPass),
                typeof(AbstractIdentityTableDerivationRelationalModelSetPass),
                typeof(ReferenceBindingRelationalModelSetPass),
                typeof(RootIdentityConstraintRelationalModelSetPass),
                typeof(ReferenceConstraintRelationalModelSetPass),
                typeof(ArrayUniquenessConstraintRelationalModelSetPass)
            );
    }

    [Test]
    public void It_should_be_stable_across_invocations()
    {
        _firstPassTypes.Should().Equal(_secondPassTypes);
    }
}
