IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'auth')
EXEC sys.sp_executesql N'CREATE SCHEMA [auth]'
GO

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'edfi')
EXEC sys.sp_executesql N'CREATE SCHEMA [edfi]'
GO

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'util')
EXEC sys.sp_executesql N'CREATE SCHEMA [util]'
GO
