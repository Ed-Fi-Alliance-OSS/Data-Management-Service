// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Binding_Classifier
{
    [Test]
    public void It_classifies_document_id_as_storage_managed()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId)",
            UpdateSql: "update edfi.\"School\" set \"DocumentId\" = @DocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 1, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, []);

        result.Should().HaveCount(1);
        result[0].Should().Be(BindingClassification.StorageManaged);
    }

    [Test]
    public void It_classifies_parent_key_part_as_storage_managed()
    {
        var tableModel = CreateExtensionTableModel("SchoolExtension");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolExtension\" values (@DocumentId)",
            UpdateSql: "update sample.\"SchoolExtension\" set \"DocumentId\" = @DocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: "delete from sample.\"SchoolExtension\" where \"DocumentId\" = @DocumentId",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 1, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, []);

        result.Should().HaveCount(1);
        result[0].Should().Be(BindingClassification.StorageManaged);
    }

    [Test]
    public void It_classifies_ordinal_as_storage_managed()
    {
        var tableModel = CreateCollectionTableModel("SchoolAddress");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolAddress\" values (@CollectionItemId, @School_DocumentId, @Ordinal)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set \"Ordinal\" = @Ordinal where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [0, 1, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, []);

        result[2].Should().Be(BindingClassification.StorageManaged);
    }

    [Test]
    public void It_classifies_precomputed_as_storage_managed()
    {
        var tableModel = CreateCollectionTableModel("SchoolAddress");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolAddress\" values (@CollectionItemId, @School_DocumentId, @Ordinal)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set \"Ordinal\" = @Ordinal where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [0, 1, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, []);

        result[0].Should().Be(BindingClassification.StorageManaged);
    }

    [Test]
    public void It_classifies_visible_scalar_when_not_hidden()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, []);

        result[1].Should().Be(BindingClassification.VisibleWritable);
    }

    [Test]
    public void It_classifies_hidden_scalar_when_path_matches()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, ["$.name"]);

        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    [Test]
    public void It_classifies_visible_descriptor_when_not_hidden()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @GradeLevel_DescriptorId)",
            UpdateSql: "update edfi.\"School\" set \"GradeLevel_DescriptorId\" = @GradeLevel_DescriptorId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DescriptorReference(
                        new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                        new JsonPathExpression("$.gradeLevelDescriptor", [])
                    ),
                    "GradeLevel_DescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, []);

        result[1].Should().Be(BindingClassification.VisibleWritable);
    }

    [Test]
    public void It_classifies_hidden_descriptor_when_path_matches()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @GradeLevel_DescriptorId)",
            UpdateSql: "update edfi.\"School\" set \"GradeLevel_DescriptorId\" = @GradeLevel_DescriptorId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DescriptorReference(
                        new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                        new JsonPathExpression("$.gradeLevelDescriptor", [])
                    ),
                    "GradeLevel_DescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, ["$.gradeLevelDescriptor"]);

        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    [Test]
    public void It_classifies_document_reference_as_visible()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @LocalEducationAgency_DocumentId)",
            UpdateSql: "update edfi.\"School\" set \"LocalEducationAgency_DocumentId\" = @LocalEducationAgency_DocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentReference(0),
                    "LocalEducationAgency_DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, []);

        result[1].Should().Be(BindingClassification.VisibleWritable);
    }

    [Test]
    public void It_classifies_two_part_doc_ref_as_hidden_when_one_member_hidden_one_visible()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Lea_DocumentId)",
            UpdateSql: "update edfi.\"School\" set \"Lea_DocumentId\" = @Lea_DocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.DocumentReference(0),
                    "Lea_DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var documentReferenceBindings = new List<DocumentReferenceBinding>
        {
            new(
                IsIdentityComponent: false,
                ReferenceObjectPath: new JsonPathExpression("$.leaReference", []),
                Table: tableModel.Table,
                FkColumn: new DbColumnName("Lea_DocumentId"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "LocalEducationAgency"),
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaId", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaId", []),
                        Column: new DbColumnName("LeaId")
                    ),
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaCategory", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaCategory", []),
                        Column: new DbColumnName("LeaCategory")
                    ),
                ]
            ),
        };

        // Member 1 hidden, member 2 visible → HiddenPreserved
        var result = RelationalWriteBindingClassifier.Classify(
            plan,
            ["$.leaReference.leaId"],
            [],
            documentReferenceBindings
        );

        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    [Test]
    public void It_classifies_two_part_doc_ref_as_hidden_when_one_member_hidden_one_clearable()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Lea_DocumentId)",
            UpdateSql: "update edfi.\"School\" set \"Lea_DocumentId\" = @Lea_DocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.DocumentReference(0),
                    "Lea_DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var documentReferenceBindings = new List<DocumentReferenceBinding>
        {
            new(
                IsIdentityComponent: false,
                ReferenceObjectPath: new JsonPathExpression("$.leaReference", []),
                Table: tableModel.Table,
                FkColumn: new DbColumnName("Lea_DocumentId"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "LocalEducationAgency"),
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaId", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaId", []),
                        Column: new DbColumnName("LeaId")
                    ),
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaCategory", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaCategory", []),
                        Column: new DbColumnName("LeaCategory")
                    ),
                ]
            ),
        };

        // Member 1 hidden, member 2 clearable → HiddenPreserved (hidden wins)
        var result = RelationalWriteBindingClassifier.Classify(
            plan,
            ["$.leaReference.leaId"],
            ["$.leaReference.leaCategory"],
            documentReferenceBindings
        );

        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    [Test]
    public void It_classifies_two_part_doc_ref_as_visible_writable_when_one_visible_one_clearable()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Lea_DocumentId)",
            UpdateSql: "update edfi.\"School\" set \"Lea_DocumentId\" = @Lea_DocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.DocumentReference(0),
                    "Lea_DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var documentReferenceBindings = new List<DocumentReferenceBinding>
        {
            new(
                IsIdentityComponent: false,
                ReferenceObjectPath: new JsonPathExpression("$.leaReference", []),
                Table: tableModel.Table,
                FkColumn: new DbColumnName("Lea_DocumentId"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "LocalEducationAgency"),
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaId", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaId", []),
                        Column: new DbColumnName("LeaId")
                    ),
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaCategory", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaCategory", []),
                        Column: new DbColumnName("LeaCategory")
                    ),
                ]
            ),
        };

        // Member 1 visible, member 2 clearable → VisibleWritable (no hidden members)
        var result = RelationalWriteBindingClassifier.Classify(
            plan,
            [],
            ["$.leaReference.leaCategory"],
            documentReferenceBindings
        );

        result[1].Should().Be(BindingClassification.VisibleWritable);
    }

    [Test]
    public void It_classifies_three_part_doc_ref_as_hidden_when_mixed_hidden_visible_clearable()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Lea_DocumentId)",
            UpdateSql: "update edfi.\"School\" set \"Lea_DocumentId\" = @Lea_DocumentId where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.DocumentReference(0),
                    "Lea_DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        var documentReferenceBindings = new List<DocumentReferenceBinding>
        {
            new(
                IsIdentityComponent: false,
                ReferenceObjectPath: new JsonPathExpression("$.leaReference", []),
                Table: tableModel.Table,
                FkColumn: new DbColumnName("Lea_DocumentId"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "LocalEducationAgency"),
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaId", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaId", []),
                        Column: new DbColumnName("LeaId")
                    ),
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaCategory", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaCategory", []),
                        Column: new DbColumnName("LeaCategory")
                    ),
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: new JsonPathExpression("$.leaRegion", []),
                        ReferenceJsonPath: new JsonPathExpression("$.leaReference.leaRegion", []),
                        Column: new DbColumnName("LeaRegion")
                    ),
                ]
            ),
        };

        // Member 1 hidden, member 2 visible, member 3 clearable → HiddenPreserved
        var result = RelationalWriteBindingClassifier.Classify(
            plan,
            ["$.leaReference.leaId"],
            ["$.leaReference.leaRegion"],
            documentReferenceBindings
        );

        result[1].Should().Be(BindingClassification.HiddenPreserved);
    }

    [Test]
    public void It_classifies_mixed_visible_and_hidden_on_same_table()
    {
        var tableModel = CreateRootTableModel("School");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SchoolId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"SchoolId\" = @SchoolId, \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.schoolId", []),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );

        var result = RelationalWriteBindingClassifier.Classify(plan, ["$.name"]);

        result.Should().HaveCount(3);
        result[0].Should().Be(BindingClassification.StorageManaged);
        result[1].Should().Be(BindingClassification.VisibleWritable);
        result[2].Should().Be(BindingClassification.HiddenPreserved);
    }

    [Test]
    public void It_validates_collection_key_binding_as_storage_managed()
    {
        var tableModel = CreateCollectionTableModel("SchoolAddress");

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolAddress\" values (@CollectionItemId, @School_DocumentId, @Ordinal)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set \"Ordinal\" = @Ordinal where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [0, 1, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var classifications = RelationalWriteBindingClassifier.Classify(plan, []);

        var act = () => RelationalWriteBindingClassifier.ValidateCollectionKeyBinding(plan, classifications);

        act.Should().NotThrow();
    }

    [Test]
    public void It_classifies_key_unification_synthetic_columns_as_clearable_when_all_members_are_clearable()
    {
        var tableModel = CreateRootTableModelWithKeyUnificationSyntheticColumns();

        var plan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@p)",
            UpdateSql: "update edfi.\"School\" set @p where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 6, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.inlined.primaryType", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "PrimaryType"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.inlined.secondaryType", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "SecondaryType"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Precomputed(),
                    "PrimaryType_Unified"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Precomputed(),
                    "PrimaryType_Present"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.Precomputed(),
                    "SecondaryType_Present"
                ),
            ],
            KeyUnificationPlans:
            [
                new KeyUnificationWritePlan(
                    CanonicalColumn: new DbColumnName("PrimaryType_Unified"),
                    CanonicalBindingIndex: 3,
                    MembersInOrder:
                    [
                        new KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: new DbColumnName("PrimaryType"),
                            RelativePath: new JsonPathExpression("$.inlined.primaryType", []),
                            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                            PresenceColumn: new DbColumnName("PrimaryType_Present"),
                            PresenceBindingIndex: 4,
                            PresenceIsSynthetic: true
                        ),
                        new KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: new DbColumnName("SecondaryType"),
                            RelativePath: new JsonPathExpression("$.inlined.secondaryType", []),
                            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                            PresenceColumn: new DbColumnName("SecondaryType_Present"),
                            PresenceBindingIndex: 5,
                            PresenceIsSynthetic: true
                        ),
                    ]
                ),
            ]
        );

        var result = RelationalWriteBindingClassifier.Classify(
            plan,
            hiddenMemberPaths: [],
            clearableMemberPaths: ["$.inlined.primaryType", "$.inlined.secondaryType"],
            documentReferenceBindings: null
        );

        result[0].Should().Be(BindingClassification.StorageManaged);
        result[3].Should().Be(BindingClassification.ClearOnVisibleAbsent);
        result[4].Should().Be(BindingClassification.ClearOnVisibleAbsent);
        result[5].Should().Be(BindingClassification.ClearOnVisibleAbsent);
    }

    private static DbTableModel CreateRootTableModel(string tableName)
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("SchoolId", ColumnKind.Scalar),
                CreateColumn("Name", ColumnKind.Scalar),
                CreateColumn("GradeLevel_DescriptorId", ColumnKind.DescriptorFk),
                CreateColumn("LocalEducationAgency_DocumentId", ColumnKind.DocumentFk),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };
    }

    private static DbTableModel CreateExtensionTableModel(string tableName)
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), tableName),
            new JsonPathExpression("$._ext.sample", []),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("ExtensionCode", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                []
            ),
        };
    }

    private static DbTableModel CreateCollectionTableModel(string tableName)
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                []
            ),
        };
    }

    private static DbTableModel CreateRootTableModelWithKeyUnificationSyntheticColumns()
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("PrimaryType", ColumnKind.Scalar),
                CreateColumn("SecondaryType", ColumnKind.Scalar),
                CreateColumn("PrimaryType_Unified", ColumnKind.Scalar, isNullable: true),
                CreateColumn("PrimaryType_Present", ColumnKind.Scalar, isNullable: true),
                CreateColumn("SecondaryType_Present", ColumnKind.Scalar, isNullable: true),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };
    }

    private static DbColumnModel CreateColumn(string name, ColumnKind kind, bool isNullable = false)
    {
        return new DbColumnModel(
            new DbColumnName(name),
            kind,
            kind is ColumnKind.Scalar or ColumnKind.Ordinal
                ? new RelationalScalarType(ScalarKind.String)
                : null,
            isNullable,
            null,
            null,
            new ColumnStorage.Stored()
        );
    }
}
