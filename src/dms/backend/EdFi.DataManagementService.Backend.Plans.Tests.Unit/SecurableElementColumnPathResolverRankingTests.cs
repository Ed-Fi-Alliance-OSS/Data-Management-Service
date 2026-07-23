// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class SecurableElementColumnPathResolverRankingTests
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(short id, string project, string resource) =>
        new(id, new QualifiedResourceName(project, resource), "1.0", false);

    private static DbTableModel CreateRootTable(
        DbTableName table,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            Path("$"),
            new TableKey("PK_Test", [new DbKeyColumn(Col("DocumentId"), ColumnKind.Scalar)]),
            columns ?? [],
            []
        );

    private static RelationalResourceModel CreateModel(
        string project,
        string resource,
        DbTableModel root,
        IReadOnlyList<DocumentReferenceBinding>? bindings = null
    ) =>
        new(
            new QualifiedResourceName(project, resource),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            bindings ?? [],
            []
        );

    private static ConcreteResourceModel CreateConcrete(
        short keyId,
        string project,
        string resource,
        RelationalResourceModel model,
        ResourceSecurableElements? securableElements = null
    ) =>
        new(ResourceKey(keyId, project, resource), ResourceStorageKind.RelationalTables, model)
        {
            SecurableElements = securableElements ?? ResourceSecurableElements.Empty,
        };

    [Test]
    public void Identity_preferred_over_non_identity()
    {
        // Start -> Middle -> Student
        // Middle has two bindings to Student: non-identity (first) and identity (second).
        // Expect the identity-path to be selected even if listed after the non-identity binding.

        var startRoot = CreateRootTable(
            Table("Start"),
            [
                new DbColumnModel(
                    Col("Middle_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Middle")
                ),
                new DbColumnModel(Col("Middle_StudentUnique"), ColumnKind.Scalar, null, false, null, null),
            ]
        );

        var middleRoot = CreateRootTable(
            Table("Middle"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId_NonIdentity"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("Student_DocumentId_Identity"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("Student_StudentUnique_Non"),
                    ColumnKind.Scalar,
                    null,
                    true,
                    null,
                    null
                ),
                new DbColumnModel(Col("Student_StudentUnique_Id"), ColumnKind.Scalar, null, true, null, null),
            ]
        );

        var studentRoot = CreateRootTable(Table("Student"));

        // Start -> Middle binding (root-level) that carries the securable identity path
        var startToMiddleBinding = new DocumentReferenceBinding(
            true,
            Path("$.middleReference"),
            startRoot.Table,
            Col("Middle_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Middle"),
            [
                new ReferenceIdentityBinding(
                    Path("$.middleReference.studentUniqueId"),
                    Path("$.middleReference.studentUniqueId"),
                    Col("Middle_StudentUnique")
                ),
            ]
        );

        // Middle -> Student non-identity binding (listed first)
        var middleToStudentNon = new DocumentReferenceBinding(
            false,
            Path("$.studentReference"),
            middleRoot.Table,
            Col("Student_DocumentId_NonIdentity"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentReference.studentUniqueId"),
                    Path("$.studentReference.studentUniqueId"),
                    Col("Student_StudentUnique_Non")
                ),
            ]
        );

        // Middle -> Student identity binding (listed second)
        var middleToStudentIdentity = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            middleRoot.Table,
            Col("Student_DocumentId_Identity"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentReference.studentUniqueId"),
                    Path("$.studentReference.studentUniqueId"),
                    Col("Student_StudentUnique_Id")
                ),
            ]
        );

        var startModel = CreateModel("Ed-Fi", "Start", startRoot, [startToMiddleBinding]);
        var middleModel = CreateModel(
            "Ed-Fi",
            "Middle",
            middleRoot,
            [middleToStudentIdentity, middleToStudentNon]
        );
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);

        var securableElements = new ResourceSecurableElements(
            [],
            [],
            ["$.middleReference.studentUniqueId"],
            [],
            []
        );

        var startConcrete = CreateConcrete(1, "Ed-Fi", "Start", startModel, securableElements);
        var middleConcrete = CreateConcrete(
            2,
            "Ed-Fi",
            "Middle",
            middleModel,
            new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.dummy", "x")],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            )
        );
        var studentConcrete = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var results = SecurableElementColumnPathResolver.ResolveAll(
            startConcrete,
            [startConcrete, middleConcrete, studentConcrete]
        );

        results.Should().ContainSingle();
        var path = results[0];
        path.Kind.Should().Be(SecurableElementKind.Student);
        path.Steps.Should().HaveCount(2);

        // The second hop should use the identity binding's FK column when identity is preferred.
        path.Steps[1].SourceColumnName.Should().Be(Col("Student_DocumentId_Identity"));
    }

    [Test]
    public void Required_preferred_over_optional()
    {
        // Similar to identity test but differentiate on FK column nullability (IsNullable=false == required)
        var startRoot = CreateRootTable(
            Table("StartR"),
            [
                new DbColumnModel(
                    Col("Middle_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "MiddleR")
                ),
            ]
        );

        var middleRoot = CreateRootTable(
            Table("MiddleR"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId_Optional"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("Student_DocumentId_Required"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var studentRoot = CreateRootTable(Table("Student"));

        var startToMiddleBinding = new DocumentReferenceBinding(
            true,
            Path("$.middleReference"),
            startRoot.Table,
            Col("Middle_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "MiddleR"),
            [
                new ReferenceIdentityBinding(
                    Path("$.middleReference.studentUniqueId"),
                    Path("$.middleReference.studentUniqueId"),
                    Col("Middle_StudentUnique")
                ),
            ]
        );

        var middleToStudentOptional = new DocumentReferenceBinding(
            true,
            Path("$.studentRefOptional"),
            middleRoot.Table,
            Col("Student_DocumentId_Optional"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentRefOptional.studentUniqueId"),
                    Path("$.studentRefOptional.studentUniqueId"),
                    Col("Student_StudentUnique_Opt")
                ),
            ]
        );

        var middleToStudentRequired = new DocumentReferenceBinding(
            true,
            Path("$.studentRefRequired"),
            middleRoot.Table,
            Col("Student_DocumentId_Required"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentRefRequired.studentUniqueId"),
                    Path("$.studentRefRequired.studentUniqueId"),
                    Col("Student_StudentUnique_Req")
                ),
            ]
        );

        var startModel = CreateModel("Ed-Fi", "StartR", startRoot, [startToMiddleBinding]);
        var middleModel = CreateModel(
            "Ed-Fi",
            "MiddleR",
            middleRoot,
            [middleToStudentOptional, middleToStudentRequired]
        );
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);

        var securableElements = new ResourceSecurableElements(
            [],
            [],
            ["$.middleReference.studentUniqueId"],
            [],
            []
        );

        var startConcrete = CreateConcrete(1, "Ed-Fi", "StartR", startModel, securableElements);
        var middleConcrete = CreateConcrete(
            2,
            "Ed-Fi",
            "MiddleR",
            middleModel,
            new ResourceSecurableElements([], [], ["$.studentRefRequired.studentUniqueId"], [], [])
        );
        var studentConcrete = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var results = SecurableElementColumnPathResolver.ResolveAll(
            startConcrete,
            [startConcrete, middleConcrete, studentConcrete]
        );

        results.Should().ContainSingle();
        var path = results[0];
        path.Kind.Should().Be(SecurableElementKind.Student);
        path.Steps.Should().HaveCount(2);

        // Prefer the required FK (non-nullable) over the optional one.
        path.Steps[1].SourceColumnName.Should().Be(Col("Student_DocumentId_Required"));
    }

    [Test]
    public void Non_role_named_preferred_over_role_named()
    {
        // Middle has two bindings to Student that differ by reference-object naming style.
        var startRoot = CreateRootTable(
            Table("StartNR"),
            [
                new DbColumnModel(
                    Col("Middle_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "MiddleNR")
                ),
            ]
        );
        var middleRoot = CreateRootTable(
            Table("MiddleNR"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId_RoleNamed"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("Student_DocumentId_NonRole"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var studentRoot = CreateRootTable(Table("Student"));

        var startToMiddleBinding = new DocumentReferenceBinding(
            true,
            Path("$.middleReference"),
            startRoot.Table,
            Col("Middle_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "MiddleNR"),
            [
                new ReferenceIdentityBinding(
                    Path("$.middleReference.studentUniqueId"),
                    Path("$.middleReference.studentUniqueId"),
                    Col("Middle_StudentUnique")
                ),
            ]
        );

        // role-named style reference object path
        var middleToStudentRoleNamed = new DocumentReferenceBinding(
            true,
            Path("$.mentorStudentReference"),
            middleRoot.Table,
            Col("Student_DocumentId_RoleNamed"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.mentorStudentReference.studentUniqueId"),
                    Path("$.mentorStudentReference.studentUniqueId"),
                    Col("Student_StudentUnique_Role")
                ),
            ],
            IsRoleNamed: true
        );

        // non-role-named style reference object path
        var middleToStudentNonRole = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            middleRoot.Table,
            Col("Student_DocumentId_NonRole"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentReference.studentUniqueId"),
                    Path("$.studentReference.studentUniqueId"),
                    Col("Student_StudentUnique_NonRole")
                ),
            ]
        );

        var startModel = CreateModel("Ed-Fi", "StartNR", startRoot, [startToMiddleBinding]);
        var middleModel = CreateModel(
            "Ed-Fi",
            "MiddleNR",
            middleRoot,
            [middleToStudentNonRole, middleToStudentRoleNamed]
        );
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);

        var startConcrete = CreateConcrete(
            1,
            "Ed-Fi",
            "StartNR",
            startModel,
            new ResourceSecurableElements([], [], ["$.middleReference.studentUniqueId"], [], [])
        );
        var middleConcrete = CreateConcrete(
            2,
            "Ed-Fi",
            "MiddleNR",
            middleModel,
            new ResourceSecurableElements([], [], ["$.studentReference.studentUniqueId"], [], [])
        );
        var studentConcrete = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var results = SecurableElementColumnPathResolver.ResolveAll(
            startConcrete,
            [startConcrete, middleConcrete, studentConcrete]
        );

        results.Should().ContainSingle();
        var path = results[0];
        path.Kind.Should().Be(SecurableElementKind.Student);
        path.Steps.Should().HaveCount(2);

        // Prefer the non-role-named binding
        path.Steps[1].SourceColumnName.Should().Be(Col("Student_DocumentId_NonRole"));
    }

    [Test]
    public void Shorter_path_tiebreaker()
    {
        // Subject declares two root-level person paths: one direct (1 hop), one transitive (2 hops).
        var root = CreateRootTable(
            Table("RootShort"),
            [
                new DbColumnModel(
                    Col("DirectStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("ViaMiddle_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "MiddleShort")
                ),
            ]
        );

        var middle = CreateRootTable(
            Table("MiddleShort"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var student = CreateRootTable(Table("Student"));

        var directBinding = new DocumentReferenceBinding(
            true,
            Path("$.directStudentReference"),
            root.Table,
            Col("DirectStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.directStudentReference.studentUniqueId"),
                    Path("$.directStudentReference.studentUniqueId"),
                    Col("DirectStudent_StudentUnique")
                ),
            ]
        );

        var viaBinding = new DocumentReferenceBinding(
            true,
            Path("$.viaMiddleReference"),
            root.Table,
            Col("ViaMiddle_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "MiddleShort"),
            [
                new ReferenceIdentityBinding(
                    Path("$.viaMiddleReference.studentUniqueId"),
                    Path("$.viaMiddleReference.studentUniqueId"),
                    Col("ViaMiddle_StudentUnique")
                ),
            ]
        );

        var middleToStudent = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            middle.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentReference.studentUniqueId"),
                    Path("$.studentReference.studentUniqueId"),
                    Col("Middle_StudentUnique")
                ),
            ]
        );

        var rootModel = CreateModel("Ed-Fi", "RootShort", root, [directBinding, viaBinding]);
        var middleModel = CreateModel("Ed-Fi", "MiddleShort", middle, [middleToStudent]);
        var studentModel = CreateModel("Ed-Fi", "Student", student);

        var securableElements = new ResourceSecurableElements(
            [],
            [],
            ["$.directStudentReference.studentUniqueId", "$.viaMiddleReference.studentUniqueId"],
            [],
            []
        );

        var rootConcrete = CreateConcrete(1, "Ed-Fi", "RootShort", rootModel, securableElements);
        var middleConcrete = CreateConcrete(
            2,
            "Ed-Fi",
            "MiddleShort",
            middleModel,
            new ResourceSecurableElements([], [], ["$.studentReference.studentUniqueId"], [], [])
        );
        var studentConcrete = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var results = SecurableElementColumnPathResolver.ResolveAll(
            rootConcrete,
            [rootConcrete, middleConcrete, studentConcrete]
        );

        results.Should().HaveCount(2);
        results
            .Should()
            .Contain(path =>
                path.Kind == SecurableElementKind.Student
                && path.Steps.Count == 1
                && path.Steps[0].SourceColumnName == Col("DirectStudent_DocumentId")
            );
        results
            .Should()
            .Contain(path =>
                path.Kind == SecurableElementKind.Student
                && path.Steps.Count == 2
                && path.Steps[0].SourceColumnName == Col("ViaMiddle_DocumentId")
            );
    }

    [Test]
    public void Non_identity_intermediate_hop_is_rejected_and_alternative_chosen()
    {
        // Start -> MidA -> Dead (non-identity hop) and Start -> MidB -> Student (valid)
        var start = CreateRootTable(
            Table("StartNI"),
            [
                new DbColumnModel(
                    Col("MidA_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "MidA")
                ),
                new DbColumnModel(
                    Col("MidB_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "MidB")
                ),
            ]
        );

        var midA = CreateRootTable(
            Table("MidA"),
            [
                new DbColumnModel(
                    Col("Dead_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Dead")
                ),
            ]
        );
        var midB = CreateRootTable(
            Table("MidB"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var dead = CreateRootTable(Table("Dead"));
        var student = CreateRootTable(Table("Student"));

        var startToMidA = new DocumentReferenceBinding(
            true,
            Path("$.midAReference"),
            start.Table,
            Col("MidA_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "MidA"),
            [
                new ReferenceIdentityBinding(
                    Path("$.midAReference.someId"),
                    Path("$.midAReference.someId"),
                    Col("MidA_SomeId")
                ),
            ]
        );
        var startToMidB = new DocumentReferenceBinding(
            true,
            Path("$.midBReference"),
            start.Table,
            Col("MidB_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "MidB"),
            [
                new ReferenceIdentityBinding(
                    Path("$.midBReference.studentUniqueId"),
                    Path("$.midBReference.studentUniqueId"),
                    Col("MidB_StudentUnique")
                ),
            ]
        );

        // MidA -> Dead is non-identity (should be rejected as intermediate)
        var midAToDead = new DocumentReferenceBinding(
            false,
            Path("$.deadReference"),
            midA.Table,
            Col("Dead_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Dead"),
            [
                new ReferenceIdentityBinding(
                    Path("$.deadReference.x"),
                    Path("$.deadReference.x"),
                    Col("Dead_X")
                ),
            ]
        );

        // MidB -> Student is identity
        var midBToStudent = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            midB.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentReference.studentUniqueId"),
                    Path("$.studentReference.studentUniqueId"),
                    Col("StudentNI_StudentUnique")
                ),
            ]
        );

        var startModel = CreateModel("Ed-Fi", "StartNI", start, [startToMidA, startToMidB]);
        var midAModel = CreateModel("Ed-Fi", "MidA", midA, [midAToDead]);
        var midBModel = CreateModel("Ed-Fi", "MidB", midB, [midBToStudent]);
        var deadModel = CreateModel("Ed-Fi", "Dead", dead);
        var studentModel = CreateModel("Ed-Fi", "Student", student);

        var securableElements = new ResourceSecurableElements(
            [],
            [],
            ["$.midBReference.studentUniqueId"],
            [],
            []
        );

        var startConcrete = CreateConcrete(1, "Ed-Fi", "StartNI", startModel, securableElements);
        var midAConcrete = CreateConcrete(2, "Ed-Fi", "MidA", midAModel);
        var midBConcrete = CreateConcrete(
            3,
            "Ed-Fi",
            "MidB",
            midBModel,
            new ResourceSecurableElements([], [], ["$.studentReference.studentUniqueId"], [], [])
        );
        var deadConcrete = CreateConcrete(4, "Ed-Fi", "Dead", deadModel);
        var studentConcrete = CreateConcrete(5, "Ed-Fi", "Student", studentModel);

        var results = SecurableElementColumnPathResolver.ResolveAll(
            startConcrete,
            [startConcrete, midAConcrete, midBConcrete, deadConcrete, studentConcrete]
        );

        results.Should().ContainSingle();
        var path = results[0];
        path.Kind.Should().Be(SecurableElementKind.Student);
        // Should choose MidB route
        path.Steps[0].SourceTable.Should().Be(Table("StartNI"));
        path.Steps[0].SourceColumnName.Should().Be(Col("MidB_DocumentId"));
    }

    [Test]
    public void Cyclic_branch_does_not_hide_valid_alternative()
    {
        // Start -> A: A has two declared securables; one leads to B -> A (cycle), other to C -> Student.
        var start = CreateRootTable(
            Table("StartC"),
            [
                new DbColumnModel(
                    Col("A_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "A")
                ),
            ]
        );
        var a = CreateRootTable(
            Table("A"),
            [
                new DbColumnModel(
                    Col("B_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "B")
                ),
                new DbColumnModel(
                    Col("C_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "C")
                ),
            ]
        );
        var b = CreateRootTable(
            Table("B"),
            [
                new DbColumnModel(
                    Col("A_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "A")
                ),
            ]
        );
        var c = CreateRootTable(
            Table("C"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var student = CreateRootTable(Table("Student"));

        var startToA = new DocumentReferenceBinding(
            true,
            Path("$.aReference"),
            start.Table,
            Col("A_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "A"),
            [new ReferenceIdentityBinding(Path("$.aReference.x"), Path("$.aReference.x"), Col("A_X"))]
        );

        var aToB = new DocumentReferenceBinding(
            true,
            Path("$.toB"),
            a.Table,
            Col("B_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "B"),
            [new ReferenceIdentityBinding(Path("$.toB.someId"), Path("$.toB.someId"), Col("B_SomeId"))]
        );
        var aToC = new DocumentReferenceBinding(
            true,
            Path("$.toC"),
            a.Table,
            Col("C_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "C"),
            [
                new ReferenceIdentityBinding(
                    Path("$.toC.studentUniqueId"),
                    Path("$.toC.studentUniqueId"),
                    Col("C_StudentUnique")
                ),
            ]
        );

        var bToA = new DocumentReferenceBinding(
            true,
            Path("$.back"),
            b.Table,
            Col("A_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "A"),
            [new ReferenceIdentityBinding(Path("$.back.x"), Path("$.back.x"), Col("A_X_Back"))]
        );

        var cToStudent = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            c.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                new ReferenceIdentityBinding(
                    Path("$.studentReference.studentUniqueId"),
                    Path("$.studentReference.studentUniqueId"),
                    Col("Student_StudentUnique")
                ),
            ]
        );

        var startModel = CreateModel("Ed-Fi", "StartC", start, [startToA]);
        var aModel = CreateModel("Ed-Fi", "A", a, [aToB, aToC]);
        var bModel = CreateModel("Ed-Fi", "B", b, [bToA]);
        var cModel = CreateModel("Ed-Fi", "C", c, [cToStudent]);
        var studentModel = CreateModel("Ed-Fi", "Student", student);

        var securableElements = new ResourceSecurableElements([], [], ["$.aReference.x"], [], []);

        var startConcrete = CreateConcrete(1, "Ed-Fi", "StartC", startModel, securableElements);
        var aConcrete = CreateConcrete(
            2,
            "Ed-Fi",
            "A",
            aModel,
            new ResourceSecurableElements([], [], ["$.toB.someId", "$.toC.studentUniqueId"], [], [])
        );
        var bConcrete = CreateConcrete(
            3,
            "Ed-Fi",
            "B",
            bModel,
            new ResourceSecurableElements([], [], ["$.back.x"], [], [])
        );
        var cConcrete = CreateConcrete(
            4,
            "Ed-Fi",
            "C",
            cModel,
            new ResourceSecurableElements([], [], ["$.studentReference.studentUniqueId"], [], [])
        );
        var studentConcrete = CreateConcrete(5, "Ed-Fi", "Student", studentModel);

        var results = SecurableElementColumnPathResolver.ResolveAll(
            startConcrete,
            [startConcrete, aConcrete, bConcrete, cConcrete, studentConcrete]
        );

        results.Should().ContainSingle();
        var path = results[0];
        path.Kind.Should().Be(SecurableElementKind.Student);
        // Ensure the selected route goes through C (the valid branch), not the cyclic B->A branch
        path.Steps.Should().ContainSingle(s => s.SourceTable == Table("C")).Should().NotBeNull();
    }
}
