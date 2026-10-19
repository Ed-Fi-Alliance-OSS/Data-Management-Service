IF COL_LENGTH(N'testproject.Widget', N'WidgetNote') IS NULL
    ALTER TABLE [testproject].[Widget] ADD [WidgetNote] nvarchar(120) NULL;