/****** Update 0002: profile settings, plan runs, richer assignments and plan locks ******/
USE [$(DatabaseName)]
GO

-- The same columns and table are in 01_Tables.sql for databases built from scratch, safe to run twice

IF COL_LENGTH(N'dbo.Profiles', N'settings') IS NULL
BEGIN
	ALTER TABLE [dbo].[Profiles] ADD [settings] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_Profiles_settings] DEFAULT '{}'
	EXEC(N'ALTER TABLE [dbo].[Profiles] ADD CONSTRAINT [CK_Profiles_settings] CHECK (ISJSON([settings]) = 1)')
END
GO

IF COL_LENGTH(N'dbo.SolveRuns', N'kind') IS NULL
BEGIN
	ALTER TABLE [dbo].[SolveRuns] ADD
		[kind] VARCHAR(20) NOT NULL CONSTRAINT [DF_SolveRuns_kind] DEFAULT 'Solve',
		[parentRunId] VARCHAR(50) NULL,
		[published] BIT NOT NULL CONSTRAINT [DF_SolveRuns_published] DEFAULT 0,
		[publishedAt] DATETIME2 NULL,
		[discarded] BIT NOT NULL CONSTRAINT [DF_SolveRuns_discarded] DEFAULT 0,
		[details] NVARCHAR(MAX) NULL,
		[diagnostics] NVARCHAR(MAX) NULL,
		[unscheduled] NVARCHAR(MAX) NULL,
		[changes] NVARCHAR(MAX) NULL,
		[stats] NVARCHAR(MAX) NULL
	EXEC(N'ALTER TABLE [dbo].[SolveRuns] ADD CONSTRAINT [CK_SolveRuns_kind] CHECK ([kind] IN (''Solve'', ''WhatIf'', ''Emergency''))')
	ALTER TABLE [dbo].[SolveRuns] DROP CONSTRAINT [CK_SolveRuns_status]
	ALTER TABLE [dbo].[SolveRuns] ADD CONSTRAINT [CK_SolveRuns_status] CHECK ([status] IN ('Pending', 'Running', 'Optimal', 'Feasible', 'Infeasible', 'Stopped', 'Failed', 'Invalid'))
	CREATE NONCLUSTERED INDEX [IX_SolveRuns_profileId_requested] ON [dbo].[SolveRuns]([profileId], [requested])
END
GO

IF COL_LENGTH(N'dbo.ScheduleAssignments', N'segment') IS NULL
	ALTER TABLE [dbo].[ScheduleAssignments] ADD
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
		[fixed] BIT NOT NULL CONSTRAINT [DF_ScheduleAssignments_fixed] DEFAULT 0
GO

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
