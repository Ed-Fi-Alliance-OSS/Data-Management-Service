ALTER TABLE [testextension].[HairColorDescriptor] WITH CHECK ADD CONSTRAINT [FK_HairColorDescriptor_Descriptor] FOREIGN KEY ([HairColorDescriptorId])
REFERENCES [edfi].[Descriptor] ([DescriptorId])
ON DELETE CASCADE
GO

ALTER TABLE [testextension].[StudentEducationOrganizationAssociationExtension] WITH CHECK ADD CONSTRAINT [FK_StudentEducationOrganizationAssociationExtension_HairColorDescriptor] FOREIGN KEY ([HairColorDescriptorId])
REFERENCES [testextension].[HairColorDescriptor] ([HairColorDescriptorId])
GO

CREATE NONCLUSTERED INDEX [FK_StudentEducationOrganizationAssociationExtension_HairColorDescriptor]
ON [testextension].[StudentEducationOrganizationAssociationExtension] ([HairColorDescriptorId] ASC)
GO

ALTER TABLE [testextension].[StudentEducationOrganizationAssociationExtension] WITH CHECK ADD CONSTRAINT [FK_StudentEducationOrganizationAssociationExtension_StudentEducationOrganizationAssociation] FOREIGN KEY ([EducationOrganizationId], [StudentUSI])
REFERENCES [edfi].[StudentEducationOrganizationAssociation] ([EducationOrganizationId], [StudentUSI])
ON DELETE CASCADE
GO

