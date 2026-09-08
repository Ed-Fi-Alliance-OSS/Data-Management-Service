IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'testextension')
EXEC sys.sp_executesql N'CREATE SCHEMA [testextension]'
GO
