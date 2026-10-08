// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Reflection-based tests pinning the public surface of the EdFi.DataManagementService.Identity
/// assembly - the third-party identity provider contract. These read the compiled assembly directly,
/// not a copy of what IIdentityService and friends declare, so an eighth public type, a changed
/// operation signature, or a version regression fails here independent of, and in addition to, the
/// packaging-lane assertion this contract also carries.
/// </summary>
[TestFixture]
[Parallelizable]
public class IdentityContractSurfaceTests
{
    private static readonly Assembly ContractAssembly = typeof(IIdentityService).Assembly;

    /// <summary>
    /// The complete, intentional public surface of the contract. This list IS the contract - an
    /// eighth public type appearing in the assembly without a corresponding update here is exactly
    /// the regression this test exists to catch.
    /// </summary>
    private static readonly string[] _expectedPublicTypeNames =
    [
        "EdFi.DataManagementService.Identity.IIdentityService",
        "EdFi.DataManagementService.Identity.IdentityAsyncResult",
        "EdFi.DataManagementService.Identity.IdentityCapabilities",
        "EdFi.DataManagementService.Identity.IdentityError",
        "EdFi.DataManagementService.Identity.IdentityRequestContext",
        "EdFi.DataManagementService.Identity.IdentityResult",
        "EdFi.DataManagementService.Identity.IdentityResultStatus",
    ];

    /// <summary>
    /// The five identity operations, excluding the Capabilities property accessor. Interface
    /// reflection returns only the members that interface declares, so this is exactly the operation
    /// set with no System.Object noise to filter separately.
    /// </summary>
    private static readonly MethodInfo[] _operations =
    [
        .. typeof(IIdentityService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName),
    ];

    private static readonly string[] _asyncResultOperationNames =
    [
        nameof(IIdentityService.FindAsync),
        nameof(IIdentityService.SearchAsync),
    ];

    private static readonly string[] _syncResultOperationNames =
    [
        nameof(IIdentityService.CreateAsync),
        nameof(IIdentityService.GetByIdAsync),
        nameof(IIdentityService.ResultsAsync),
    ];

    private static MethodInfo OperationNamed(string name) =>
        _operations.Single(method => method.Name == name);

    // ---------------------------------------------------------------- public type set

    [Test]
    public void It_exposes_exactly_the_seven_contract_types_and_no_others()
    {
        ContractAssembly
            .GetExportedTypes()
            .Select(type => type.FullName)
            .Should()
            .BeEquivalentTo(_expectedPublicTypeNames);
    }

    // ---------------------------------------------------------------- operation return types

    [Test]
    public void It_declares_exactly_five_operations_on_the_contract_interface()
    {
        _operations.Should().HaveCount(5);
    }

    [TestCaseSource(nameof(_asyncResultOperationNames))]
    public void It_returns_Task_of_IdentityAsyncResult(string operationName)
    {
        OperationNamed(operationName).ReturnType.Should().Be(typeof(Task<IdentityAsyncResult>));
    }

    [TestCaseSource(nameof(_syncResultOperationNames))]
    public void It_returns_Task_of_IdentityResult(string operationName)
    {
        OperationNamed(operationName).ReturnType.Should().Be(typeof(Task<IdentityResult>));
    }

    // ---------------------------------------------------------------- operation parameter shape

    /// <summary>
    /// Every operation's last two parameters are, in order, the request context and the cancellation
    /// token - the shape a provider implementer compiles against regardless of which operation they
    /// are implementing.
    /// </summary>
    [TestCase(nameof(IIdentityService.CreateAsync))]
    [TestCase(nameof(IIdentityService.GetByIdAsync))]
    [TestCase(nameof(IIdentityService.FindAsync))]
    [TestCase(nameof(IIdentityService.SearchAsync))]
    [TestCase(nameof(IIdentityService.ResultsAsync))]
    public void It_takes_an_IdentityRequestContext_then_a_CancellationToken_as_its_last_two_parameters(
        string operationName
    )
    {
        ParameterInfo[] parameters = OperationNamed(operationName).GetParameters();

        parameters.Should().HaveCountGreaterThanOrEqualTo(2);
        parameters[^2].ParameterType.Should().Be(typeof(IdentityRequestContext));
        parameters[^1].ParameterType.Should().Be(typeof(CancellationToken));
    }

    // ---------------------------------------------------------------- result record shape

    [Test]
    public void IdentityResult_has_no_RequestToken_property()
    {
        typeof(IdentityResult).GetProperty("RequestToken").Should().BeNull();
    }

    [Test]
    public void IdentityAsyncResult_has_a_RequestToken_property()
    {
        typeof(IdentityAsyncResult).GetProperty("RequestToken").Should().NotBeNull();
    }

    // ---------------------------------------------------------------- version independence (D5)

    [Test]
    public void The_contract_assembly_version_is_1_0_0_0()
    {
        ContractAssembly.GetName().Version.Should().Be(new Version(1, 0, 0, 0));
    }

    /// <summary>
    /// The half that proves the pinned 1.0.0.0 is independence rather than coincidence. The contract
    /// declares its own version block directly in its csproj, ahead of Directory.Build.props in
    /// MSBuild evaluation order, precisely so that SetDMSAssemblyInfo regenerating
    /// src/dms/Directory.Build.props with the DMS release version on every BuildAndPublish never
    /// reaches it. Without this half, deleting that csproj version block would leave the contract
    /// silently inheriting Core's version and this test would keep passing by coincidence whenever
    /// that inherited version happened to also read 1.0.0.0.
    /// </summary>
    [Test]
    public void The_contract_assembly_version_differs_from_Cores_own_assembly_version()
    {
        Version? coreVersion = typeof(DmsCoreServiceExtensions).Assembly.GetName().Version;

        ContractAssembly.GetName().Version.Should().NotBe(coreVersion);
    }
}
