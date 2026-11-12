/****** Update 0003: domain user security, the roles users have and their own settings ******/
USE [$(DatabaseName)]
GO

-- The same tables are in 01_Tables.sql for databases built from scratch, safe to run twice

-- Who can do what, given to Windows domain users or groups, e.g. CONTOSO\jsmith or CONTOSO\Planners
-- SiteOwner is for the whole site (no profileId), the project roles are for one profile:
-- ProjectOwner changes everything in it and says who else can use it, ProjectReader sees it all but changes nothing,
-- ProjectSolver only sees and works the schedule (solve, publish, locks, solve settings)
IF OBJECT_ID(N'dbo.UserRoles', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[UserRoles](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_UserRoles_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[userName] NVARCHAR(256) NOT NULL,
		[profileId] VARCHAR(50) NULL,
		[role] VARCHAR(20) NOT NULL,
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_UserRoles_created] DEFAULT SYSUTCDATETIME(),
		[createdBy] NVARCHAR(256) NULL,
		CONSTRAINT [PK_UserRoles] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_UserRoles_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [CK_UserRoles_role] CHECK ([role] IN ('SiteOwner', 'ProjectOwner', 'ProjectReader', 'ProjectSolver')),
		CONSTRAINT [CK_UserRoles_scope] CHECK (([role] = 'SiteOwner' AND [profileId] IS NULL) OR ([role] <> 'SiteOwner' AND [profileId] IS NOT NULL))
	)
	CREATE UNIQUE NONCLUSTERED INDEX [UX_UserRoles_userName_role_profileId] ON [dbo].[UserRoles]([userName], [role], [profileId])
END
GO

-- Each user's own screen settings, e.g. { propertiesCollapsed }, so they follow the user from machine to machine
IF OBJECT_ID(N'dbo.UserSettings', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[UserSettings](
		[userName] NVARCHAR(256) NOT NULL,
		[settings] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_UserSettings_settings] DEFAULT '{}',
		[modified] DATETIME2 NOT NULL CONSTRAINT [DF_UserSettings_modified] DEFAULT SYSUTCDATETIME(),
		CONSTRAINT [PK_UserSettings] PRIMARY KEY CLUSTERED ([userName]),
		CONSTRAINT [CK_UserSettings_settings] CHECK (ISJSON([settings]) = 1)
	)
END
GO
