INSERT INTO [edfi].[StudentSchoolAssociation]
(
    [SchoolId],
    [SchoolYear],
    [StudentUniqueId]
)
VALUES
(
    @schoolId,
    @schoolYear,
    @studentUniqueId
)
;
