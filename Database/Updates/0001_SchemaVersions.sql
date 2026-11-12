/****** Update 0001: the SchemaVersions table ******/
USE [$(DatabaseName)]
GO

-- The first update, it only adds SchemaVersions so later updates can be recorded
-- the same table is in 01_Tables.sql for databases built from scratch
IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[SchemaVersions](
		[version] INT NOT NULL,
		[name] NVARCHAR(200) NOT NULL,
		[applied] DATETIME2 NOT NULL CONSTRAINT [DF_SchemaVersions_applied] DEFAULT SYSUTCDATETIME(),
		CONSTRAINT [PK_SchemaVersions] PRIMARY KEY CLUSTERED ([version])
	)
END
GO
