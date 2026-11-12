/****** Object:  Tables for SchedulerMeta ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Every id is a guid held as text, CAST(NEWID() AS VARCHAR(50)), apart from the small lookup tables
-- This is the only database the scheduler writes to, sources are only ever read

-- Which update scripts have been run, see Updates
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

-- A scheduler set up for one process, e.g. Press Scheduling, everything else hangs off one
-- timeUnitMinutes is the size of the solver's time buckets
IF OBJECT_ID(N'dbo.Profiles', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[Profiles](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_Profiles_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[name] NVARCHAR(200) NOT NULL,
		[description] NVARCHAR(2000) NULL,
		[timeUnitMinutes] INT NOT NULL CONSTRAINT [DF_Profiles_timeUnitMinutes] DEFAULT 15,
		[settings] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_Profiles_settings] DEFAULT '{}',
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_Profiles_created] DEFAULT SYSUTCDATETIME(),
		[modified] DATETIME2 NULL,
		CONSTRAINT [PK_Profiles] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [CK_Profiles_timeUnitMinutes] CHECK ([timeUnitMinutes] BETWEEN 1 AND 1440),
		CONSTRAINT [CK_Profiles_settings] CHECK (ISJSON([settings]) = 1)
	)
	CREATE UNIQUE NONCLUSTERED INDEX [UX_Profiles_name] ON [dbo].[Profiles]([name])
END
GO

-- The kinds of connection, SQLSERVER for now, DEMO reads the src schema of this database
IF OBJECT_ID(N'dbo.ConnectionTypes', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[ConnectionTypes](
		[id] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(100) NOT NULL,
		CONSTRAINT [PK_ConnectionTypes] PRIMARY KEY CLUSTERED ([id])
	)
END
GO

-- Read only connections to where the data comes from, shared by every profile
-- settings is json { server, database, user, trustServerCertificate }, no user means Windows sign in
-- secret is the password encrypted by the API, it's never sent back, the client only learns whether there is one
IF OBJECT_ID(N'dbo.Connections', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[Connections](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_Connections_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[name] NVARCHAR(200) NOT NULL,
		[typeId] VARCHAR(50) NOT NULL,
		[settings] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_Connections_settings] DEFAULT '{}',
		[secret] NVARCHAR(MAX) NULL,
		[lastTested] DATETIME2 NULL,
		[lastTestOk] BIT NULL,
		[lastTestMessage] NVARCHAR(2000) NULL,
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_Connections_created] DEFAULT SYSUTCDATETIME(),
		[modified] DATETIME2 NULL,
		CONSTRAINT [PK_Connections] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_Connections_ConnectionTypes] FOREIGN KEY ([typeId]) REFERENCES [dbo].[ConnectionTypes]([id]),
		CONSTRAINT [CK_Connections_settings] CHECK (ISJSON([settings]) = 1)
	)
	CREATE UNIQUE NONCLUSTERED INDEX [UX_Connections_name] ON [dbo].[Connections]([name])
END
GO

-- A table, view or saved select on a connection that a profile reads from
-- columns is json [{ name, sqlType, nullable }] as last read from the connection
IF OBJECT_ID(N'dbo.Sources', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[Sources](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_Sources_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NOT NULL,
		[connectionId] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(200) NOT NULL,
		[kind] VARCHAR(10) NOT NULL,
		[schema] NVARCHAR(128) NULL,
		[objectName] NVARCHAR(128) NULL,
		[queryText] NVARCHAR(MAX) NULL,
		[columns] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_Sources_columns] DEFAULT '[]',
		[columnsRead] DATETIME2 NULL,
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_Sources_created] DEFAULT SYSUTCDATETIME(),
		[modified] DATETIME2 NULL,
		CONSTRAINT [PK_Sources] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_Sources_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [FK_Sources_Connections] FOREIGN KEY ([connectionId]) REFERENCES [dbo].[Connections]([id]),
		CONSTRAINT [CK_Sources_kind] CHECK ([kind] IN ('table', 'view', 'query')),
		CONSTRAINT [CK_Sources_columns] CHECK (ISJSON([columns]) = 1)
	)
	CREATE UNIQUE NONCLUSTERED INDEX [UX_Sources_profileId_name] ON [dbo].[Sources]([profileId], [name])
END
GO

-- The kinds of thing in a profile, e.g. Job, Press, Tool, Compound
-- kind is Item (gets scheduled), Resource (things are scheduled on or use it) or Reference (looked up)
IF OBJECT_ID(N'dbo.EntityTypes', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[EntityTypes](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_EntityTypes_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(100) NOT NULL,
		[kind] VARCHAR(20) NOT NULL,
		[description] NVARCHAR(2000) NULL,
		[sourceId] VARCHAR(50) NULL,
		[durationFormula] NVARCHAR(1000) NULL,
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_EntityTypes_created] DEFAULT SYSUTCDATETIME(),
		[modified] DATETIME2 NULL,
		CONSTRAINT [PK_EntityTypes] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_EntityTypes_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [FK_EntityTypes_Sources] FOREIGN KEY ([sourceId]) REFERENCES [dbo].[Sources]([id]),
		CONSTRAINT [CK_EntityTypes_kind] CHECK ([kind] IN ('Item', 'Resource', 'Reference'))
	)
	CREATE UNIQUE NONCLUSTERED INDEX [UX_EntityTypes_profileId_name] ON [dbo].[EntityTypes]([profileId], [name])
END
GO

-- The fields of an entity type, the role says what it means to the solver, e.g. Id, Quantity, DueDate
-- sourceColumn is the column of the entity type's source it's read from
IF OBJECT_ID(N'dbo.Attributes', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[Attributes](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_Attributes_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[entityTypeId] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(100) NOT NULL,
		[dataType] VARCHAR(20) NOT NULL,
		[role] VARCHAR(20) NOT NULL CONSTRAINT [DF_Attributes_role] DEFAULT 'Custom',
		[sourceColumn] NVARCHAR(128) NULL,
		[referencesEntityTypeId] VARCHAR(50) NULL,
		[sortOrder] INT NOT NULL CONSTRAINT [DF_Attributes_sortOrder] DEFAULT 0,
		CONSTRAINT [PK_Attributes] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_Attributes_EntityTypes] FOREIGN KEY ([entityTypeId]) REFERENCES [dbo].[EntityTypes]([id]) ON DELETE CASCADE,
		CONSTRAINT [FK_Attributes_EntityTypes_references] FOREIGN KEY ([referencesEntityTypeId]) REFERENCES [dbo].[EntityTypes]([id]),
		CONSTRAINT [CK_Attributes_dataType] CHECK ([dataType] IN ('STRING', 'INTEGER', 'DECIMAL', 'BOOLEAN', 'DATETIME')),
		CONSTRAINT [CK_Attributes_role] CHECK ([role] IN ('Custom', 'Id', 'Name', 'Duration', 'Quantity', 'CycleTime', 'Capacity', 'Copies',
			'Size', 'Category', 'ReleaseDate', 'DueDate', 'Priority', 'ParentItem', 'Sequence'))
	)
	CREATE UNIQUE NONCLUSTERED INDEX [UX_Attributes_entityTypeId_name] ON [dbo].[Attributes]([entityTypeId], [name])
END
GO

-- A named set of values that can go in a relationship, e.g. Yes / No, no profile means it's built in
IF OBJECT_ID(N'dbo.ValueScales', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[ValueScales](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_ValueScales_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NULL,
		[name] NVARCHAR(100) NOT NULL,
		CONSTRAINT [PK_ValueScales] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_ValueScales_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE
	)
END
GO

-- The values on a scale and what each means to the solver
-- meaning is Forbidden (never), Allowed, Penalty (allowed but costs weight) or Numeric (the value itself is used)
IF OBJECT_ID(N'dbo.ValueScaleValues', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[ValueScaleValues](
		[valueScaleId] VARCHAR(50) NOT NULL,
		[code] NVARCHAR(50) NOT NULL,
		[label] NVARCHAR(100) NOT NULL,
		[meaning] VARCHAR(20) NOT NULL,
		[weight] INT NOT NULL CONSTRAINT [DF_ValueScaleValues_weight] DEFAULT 0,
		[colour] VARCHAR(20) NULL,
		[sortOrder] INT NOT NULL CONSTRAINT [DF_ValueScaleValues_sortOrder] DEFAULT 0,
		CONSTRAINT [PK_ValueScaleValues] PRIMARY KEY CLUSTERED ([valueScaleId], [code]),
		CONSTRAINT [FK_ValueScaleValues_ValueScales] FOREIGN KEY ([valueScaleId]) REFERENCES [dbo].[ValueScales]([id]) ON DELETE CASCADE,
		CONSTRAINT [CK_ValueScaleValues_meaning] CHECK ([meaning] IN ('Forbidden', 'Allowed', 'Penalty', 'Numeric'))
	)
END
GO

-- Entity type A against entity type B with a value from a scale for each pair, edited as a matrix or a list
-- drives is the rule block it feeds, e.g. AssignTo, the values can be typed in or read from a source
IF OBJECT_ID(N'dbo.Relationships', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[Relationships](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_Relationships_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(200) NOT NULL,
		[entityTypeAId] VARCHAR(50) NOT NULL,
		[entityTypeBId] VARCHAR(50) NOT NULL,
		[valueScaleId] VARCHAR(50) NOT NULL,
		[defaultValue] NVARCHAR(50) NULL,
		[symmetric] BIT NOT NULL CONSTRAINT [DF_Relationships_symmetric] DEFAULT 0,
		[editor] VARCHAR(10) NOT NULL CONSTRAINT [DF_Relationships_editor] DEFAULT 'Matrix',
		[drives] VARCHAR(50) NULL,
		[sourceId] VARCHAR(50) NULL,
		[sourceAColumn] NVARCHAR(128) NULL,
		[sourceBColumn] NVARCHAR(128) NULL,
		[sourceValueColumn] NVARCHAR(128) NULL,
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_Relationships_created] DEFAULT SYSUTCDATETIME(),
		[modified] DATETIME2 NULL,
		CONSTRAINT [PK_Relationships] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_Relationships_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [FK_Relationships_EntityTypes_A] FOREIGN KEY ([entityTypeAId]) REFERENCES [dbo].[EntityTypes]([id]),
		CONSTRAINT [FK_Relationships_EntityTypes_B] FOREIGN KEY ([entityTypeBId]) REFERENCES [dbo].[EntityTypes]([id]),
		CONSTRAINT [FK_Relationships_ValueScales] FOREIGN KEY ([valueScaleId]) REFERENCES [dbo].[ValueScales]([id]),
		CONSTRAINT [FK_Relationships_Sources] FOREIGN KEY ([sourceId]) REFERENCES [dbo].[Sources]([id]),
		CONSTRAINT [CK_Relationships_editor] CHECK ([editor] IN ('Matrix', 'List'))
	)
	CREATE UNIQUE NONCLUSTERED INDEX [UX_Relationships_profileId_name] ON [dbo].[Relationships]([profileId], [name])
END
GO

-- One value per pair, the keys are the Id attribute of each entity
IF OBJECT_ID(N'dbo.RelationshipValues', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[RelationshipValues](
		[relationshipId] VARCHAR(50) NOT NULL,
		[aKey] NVARCHAR(100) NOT NULL,
		[bKey] NVARCHAR(100) NOT NULL,
		[value] NVARCHAR(50) NOT NULL,
		CONSTRAINT [PK_RelationshipValues] PRIMARY KEY CLUSTERED ([relationshipId], [aKey], [bKey]),
		CONSTRAINT [FK_RelationshipValues_Relationships] FOREIGN KEY ([relationshipId]) REFERENCES [dbo].[Relationships]([id]) ON DELETE CASCADE
	)
END
GO

-- Working time, a calendar can take on a parent's (Site, then each press) and can belong to one resource
IF OBJECT_ID(N'dbo.Calendars', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[Calendars](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_Calendars_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(200) NOT NULL,
		[parentCalendarId] VARCHAR(50) NULL,
		[resourceEntityTypeId] VARCHAR(50) NULL,
		[resourceKey] NVARCHAR(100) NULL,
		CONSTRAINT [PK_Calendars] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_Calendars_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [FK_Calendars_Calendars] FOREIGN KEY ([parentCalendarId]) REFERENCES [dbo].[Calendars]([id]),
		CONSTRAINT [FK_Calendars_EntityTypes] FOREIGN KEY ([resourceEntityTypeId]) REFERENCES [dbo].[EntityTypes]([id])
	)
END
GO

-- The weekly pattern, dayOfWeek is 0 Sunday to 6 Saturday
IF OBJECT_ID(N'dbo.CalendarShifts', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[CalendarShifts](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_CalendarShifts_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[calendarId] VARCHAR(50) NOT NULL,
		[dayOfWeek] TINYINT NOT NULL,
		[startTime] TIME(0) NOT NULL,
		[endTime] TIME(0) NOT NULL,
		CONSTRAINT [PK_CalendarShifts] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_CalendarShifts_Calendars] FOREIGN KEY ([calendarId]) REFERENCES [dbo].[Calendars]([id]) ON DELETE CASCADE,
		CONSTRAINT [CK_CalendarShifts_dayOfWeek] CHECK ([dayOfWeek] BETWEEN 0 AND 6)
	)
END
GO

-- Dates that differ from the pattern, kind is Down (not working) or Available (working when it normally wouldn't)
IF OBJECT_ID(N'dbo.CalendarExceptions', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[CalendarExceptions](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_CalendarExceptions_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[calendarId] VARCHAR(50) NOT NULL,
		[startTime] DATETIME2(0) NOT NULL,
		[endTime] DATETIME2(0) NOT NULL,
		[kind] VARCHAR(20) NOT NULL CONSTRAINT [DF_CalendarExceptions_kind] DEFAULT 'Down',
		[reason] NVARCHAR(500) NULL,
		CONSTRAINT [PK_CalendarExceptions] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_CalendarExceptions_Calendars] FOREIGN KEY ([calendarId]) REFERENCES [dbo].[Calendars]([id]) ON DELETE CASCADE,
		CONSTRAINT [CK_CalendarExceptions_kind] CHECK ([kind] IN ('Down', 'Available'))
	)
END
GO

-- The catalogue of rule blocks, adding a block is one row here and one compiler class in the solver
-- parameters is json describing what the block needs, used to draw its editor
IF OBJECT_ID(N'dbo.RuleTypes', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[RuleTypes](
		[id] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(100) NOT NULL,
		[category] NVARCHAR(50) NOT NULL,
		[description] NVARCHAR(1000) NULL,
		[compilesTo] NVARCHAR(500) NULL,
		[supportsHard] BIT NOT NULL CONSTRAINT [DF_RuleTypes_supportsHard] DEFAULT 1,
		[supportsSoft] BIT NOT NULL CONSTRAINT [DF_RuleTypes_supportsSoft] DEFAULT 1,
		[parameters] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_RuleTypes_parameters] DEFAULT '{}',
		[sortOrder] INT NOT NULL CONSTRAINT [DF_RuleTypes_sortOrder] DEFAULT 0,
		CONSTRAINT [PK_RuleTypes] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [CK_RuleTypes_parameters] CHECK (ISJSON([parameters]) = 1)
	)
END
GO

-- A rule in a profile: who it applies to, when, what must (Hard) or should (Soft, by weight) happen
-- blocks is the json block tree the rule builder makes
IF OBJECT_ID(N'dbo.Rules', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[Rules](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_Rules_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NOT NULL,
		[name] NVARCHAR(200) NOT NULL,
		[ruleTypeId] VARCHAR(50) NOT NULL,
		[enabled] BIT NOT NULL CONSTRAINT [DF_Rules_enabled] DEFAULT 1,
		[strength] VARCHAR(10) NOT NULL CONSTRAINT [DF_Rules_strength] DEFAULT 'Hard',
		[weight] INT NOT NULL CONSTRAINT [DF_Rules_weight] DEFAULT 0,
		[blocks] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_Rules_blocks] DEFAULT '{}',
		[sortOrder] INT NOT NULL CONSTRAINT [DF_Rules_sortOrder] DEFAULT 0,
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_Rules_created] DEFAULT SYSUTCDATETIME(),
		[modified] DATETIME2 NULL,
		CONSTRAINT [PK_Rules] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_Rules_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [FK_Rules_RuleTypes] FOREIGN KEY ([ruleTypeId]) REFERENCES [dbo].[RuleTypes]([id]),
		CONSTRAINT [CK_Rules_strength] CHECK ([strength] IN ('Hard', 'Soft')),
		CONSTRAINT [CK_Rules_blocks] CHECK (ISJSON([blocks]) = 1)
	)
END
GO

-- A run of the solver, with what it was asked and what it found
-- kind is Solve, WhatIf (a planner's move) or Emergency (a resource down), every run is a draft until it's published
-- the published run is the plan, locks keep its work in place when the next run is made
-- infeasibility is json of the rules and relationships that clash when no schedule could be found
-- details is json of the resources and their down times, diagnostics what the rule engine found, changes how it differs from the plan
IF OBJECT_ID(N'dbo.SolveRuns', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[SolveRuns](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_SolveRuns_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NOT NULL,
		[status] VARCHAR(20) NOT NULL CONSTRAINT [DF_SolveRuns_status] DEFAULT 'Pending',
		[requested] DATETIME2 NOT NULL CONSTRAINT [DF_SolveRuns_requested] DEFAULT SYSUTCDATETIME(),
		[started] DATETIME2 NULL,
		[finished] DATETIME2 NULL,
		[timeLimitSeconds] INT NOT NULL CONSTRAINT [DF_SolveRuns_timeLimitSeconds] DEFAULT 30,
		[horizonStart] DATETIME2(0) NULL,
		[horizonEnd] DATETIME2(0) NULL,
		[settings] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_SolveRuns_settings] DEFAULT '{}',
		[objective] FLOAT NULL,
		[message] NVARCHAR(2000) NULL,
		[infeasibility] NVARCHAR(MAX) NULL,
		[kind] VARCHAR(20) NOT NULL CONSTRAINT [DF_SolveRuns_kind] DEFAULT 'Solve',
		[parentRunId] VARCHAR(50) NULL,
		[published] BIT NOT NULL CONSTRAINT [DF_SolveRuns_published] DEFAULT 0,
		[publishedAt] DATETIME2 NULL,
		[discarded] BIT NOT NULL CONSTRAINT [DF_SolveRuns_discarded] DEFAULT 0,
		[details] NVARCHAR(MAX) NULL,
		[diagnostics] NVARCHAR(MAX) NULL,
		[unscheduled] NVARCHAR(MAX) NULL,
		[changes] NVARCHAR(MAX) NULL,
		[stats] NVARCHAR(MAX) NULL,
		CONSTRAINT [PK_SolveRuns] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_SolveRuns_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [CK_SolveRuns_status] CHECK ([status] IN ('Pending', 'Running', 'Optimal', 'Feasible', 'Infeasible', 'Stopped', 'Failed', 'Invalid')),
		CONSTRAINT [CK_SolveRuns_kind] CHECK ([kind] IN ('Solve', 'WhatIf', 'Emergency'))
	)
	CREATE NONCLUSTERED INDEX [IX_SolveRuns_profileId_requested] ON [dbo].[SolveRuns]([profileId], [requested])
END
GO

-- Where each item went in a run, one row per segment, an item split across tool counts or resources has several
-- poolKey and toolCount are the tool (or other pool) it drew from and how many copies, fixed means it came from locked work
-- secondaryResources is json of any other resources it used, inspections json of { kind, at, minutes }
-- attributes is json of the item's values, for colouring and the tooltip
IF OBJECT_ID(N'dbo.ScheduleAssignments', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[ScheduleAssignments](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_ScheduleAssignments_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[solveRunId] VARCHAR(50) NOT NULL,
		[itemKey] NVARCHAR(100) NOT NULL,
		[resourceKey] NVARCHAR(100) NOT NULL,
		[secondaryResources] NVARCHAR(MAX) NULL,
		[startTime] DATETIME2(0) NOT NULL,
		[endTime] DATETIME2(0) NOT NULL,
		[itemLabel] NVARCHAR(200) NULL,
		[segment] INT NOT NULL CONSTRAINT [DF_ScheduleAssignments_segment] DEFAULT 0,
		[poolKey] NVARCHAR(100) NULL,
		[toolCount] INT NOT NULL CONSTRAINT [DF_ScheduleAssignments_toolCount] DEFAULT 1,
		[cycles] BIGINT NOT NULL CONSTRAINT [DF_ScheduleAssignments_cycles] DEFAULT 0,
		[quantity] BIGINT NOT NULL CONSTRAINT [DF_ScheduleAssignments_quantity] DEFAULT 0,
		[setupMinutes] INT NOT NULL CONSTRAINT [DF_ScheduleAssignments_setupMinutes] DEFAULT 0,
		[dueTime] DATETIME2(0) NULL,
		[lateMinutes] INT NOT NULL CONSTRAINT [DF_ScheduleAssignments_lateMinutes] DEFAULT 0,
		[cost] INT NOT NULL CONSTRAINT [DF_ScheduleAssignments_cost] DEFAULT 0,
		[costReasons] NVARCHAR(MAX) NULL,
		[inspections] NVARCHAR(MAX) NULL,
		[attributes] NVARCHAR(MAX) NULL,
		[fixed] BIT NOT NULL CONSTRAINT [DF_ScheduleAssignments_fixed] DEFAULT 0,
		CONSTRAINT [PK_ScheduleAssignments] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_ScheduleAssignments_SolveRuns] FOREIGN KEY ([solveRunId]) REFERENCES [dbo].[SolveRuns]([id]) ON DELETE CASCADE
	)
	CREATE NONCLUSTERED INDEX [IX_ScheduleAssignments_solveRunId_resourceKey] ON [dbo].[ScheduleAssignments]([solveRunId], [resourceKey], [startTime])
END
GO

-- Locks on the published plan, so the next run leaves that work where it is
-- kind Window locks everything starting before lockedUntil, on one resource or all of them when resourceKey is empty
-- kind Item locks one item wherever and whenever it is
IF OBJECT_ID(N'dbo.PlanLocks', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[PlanLocks](
		[id] VARCHAR(50) NOT NULL CONSTRAINT [DF_PlanLocks_id] DEFAULT CAST(NEWID() AS VARCHAR(50)),
		[profileId] VARCHAR(50) NOT NULL,
		[kind] VARCHAR(10) NOT NULL,
		[lockedUntil] DATETIME2(0) NULL,
		[resourceKey] NVARCHAR(100) NULL,
		[itemKey] NVARCHAR(100) NULL,
		[reason] NVARCHAR(500) NULL,
		[created] DATETIME2 NOT NULL CONSTRAINT [DF_PlanLocks_created] DEFAULT SYSUTCDATETIME(),
		CONSTRAINT [PK_PlanLocks] PRIMARY KEY CLUSTERED ([id]),
		CONSTRAINT [FK_PlanLocks_Profiles] FOREIGN KEY ([profileId]) REFERENCES [dbo].[Profiles]([id]) ON DELETE CASCADE,
		CONSTRAINT [CK_PlanLocks_kind] CHECK ([kind] IN ('Window', 'Item')),
		CONSTRAINT [CK_PlanLocks_target] CHECK (([kind] = 'Window' AND [lockedUntil] IS NOT NULL) OR ([kind] = 'Item' AND [itemKey] IS NOT NULL))
	)
END
GO

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
