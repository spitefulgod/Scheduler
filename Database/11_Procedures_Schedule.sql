/****** Object:  StoredProcedures for solve runs, the published plan and its locks ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  StoredProcedure [dbo].[CreateSolveRun] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Starts a run, kind is Solve, WhatIf or Emergency, settings is what it was asked (pins, downs, alternatives, weights)
-- EXEC CreateSolveRun '<profile id>', 'Solve', NULL, '{}', 30, '2026-11-11T06:00', '2026-12-09T06:00'
CREATE OR ALTER PROCEDURE [dbo].[CreateSolveRun]
	@ProfileId VARCHAR(50),
	@Kind VARCHAR(20) = 'Solve',
	@ParentRunId VARCHAR(50) = NULL,
	@Settings NVARCHAR(MAX) = '{}',
	@TimeLimitSeconds INT = 30,
	@HorizonStart DATETIME2(0) = NULL,
	@HorizonEnd DATETIME2(0) = NULL
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1
		IF ISNULL(@Kind, '') NOT IN ('Solve', 'WhatIf', 'Emergency')
			THROW 51000, 'The kind needs to be Solve, WhatIf or Emergency', 1
		IF ISJSON(ISNULL(@Settings, '{}')) = 0
			THROW 51000, 'The settings need to be json', 1
		IF EXISTS (SELECT 1 FROM SolveRuns (NOLOCK) WHERE profileId = @ProfileId AND [status] IN ('Pending', 'Running') AND requested > DATEADD(HOUR, -1, SYSUTCDATETIME()))
			THROW 51000, 'A run is already going for this profile, stop it or wait for it to finish', 1

		DECLARE @runId VARCHAR(50) = CAST(NEWID() AS VARCHAR(50))
		INSERT INTO SolveRuns (id, profileId, kind, parentRunId, [status], settings, timeLimitSeconds, horizonStart, horizonEnd)
			VALUES (@runId, @ProfileId, @Kind, NULLIF(@ParentRunId, ''), 'Pending', ISNULL(@Settings, '{}'), ISNULL(@TimeLimitSeconds, 30), @HorizonStart, @HorizonEnd)

		SELECT dbo.GetSolveRunJson(@runId) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[SetSolveRunStatus] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Moves a run on, e.g. to Running, with a message for the status bar
-- EXEC SetSolveRunStatus '<run id>', 'Running', 'Reading the data'
CREATE OR ALTER PROCEDURE [dbo].[SetSolveRunStatus]
	@RunId VARCHAR(50),
	@Status VARCHAR(20),
	@Message NVARCHAR(2000) = NULL
AS
BEGIN
	BEGIN TRY
		UPDATE SolveRuns
			SET [status] = @Status, [message] = ISNULL(@Message, [message]), [started] = CASE WHEN @Status = 'Running' THEN ISNULL([started], SYSUTCDATETIME()) ELSE [started] END
			WHERE id = @RunId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Run not found', 1

		SELECT dbo.GetSolveRunJson(@RunId) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[FailUnfinishedSolveRuns] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Runs left Pending or Running when the API stopped can't finish, the API calls this when it starts so they don't block new runs
-- EXEC FailUnfinishedSolveRuns
CREATE OR ALTER PROCEDURE [dbo].[FailUnfinishedSolveRuns]
AS
BEGIN
	BEGIN TRY
		UPDATE SolveRuns
			SET [status] = 'Failed', [message] = 'The API stopped before the run finished', finished = SYSUTCDATETIME()
			WHERE [status] IN ('Pending', 'Running')

		SELECT (SELECT @@ROWCOUNT [failed] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[SaveSolveResult] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Keeps what a run found, its assignments replace any it had
-- @Assignments is json [{ itemKey, itemLabel, segment, resourceKey, poolKey, toolCount, cycles, quantity, startTime, endTime, setupMinutes,
-- dueTime, lateMinutes, cost, costReasons, inspections, attributes, fixed }]
-- EXEC SaveSolveResult '<run id>', 'Optimal', 1234, 'The best plan', NULL, '{}', '[]', '[]', '{}', '{}', '[]'
CREATE OR ALTER PROCEDURE [dbo].[SaveSolveResult]
	@RunId VARCHAR(50),
	@Status VARCHAR(20),
	@Objective FLOAT = NULL,
	@Message NVARCHAR(2000) = NULL,
	@Infeasibility NVARCHAR(MAX) = NULL,
	@Details NVARCHAR(MAX) = NULL,
	@Diagnostics NVARCHAR(MAX) = NULL,
	@Unscheduled NVARCHAR(MAX) = NULL,
	@Changes NVARCHAR(MAX) = NULL,
	@Stats NVARCHAR(MAX) = NULL,
	@Assignments NVARCHAR(MAX) = '[]'
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM SolveRuns (NOLOCK) WHERE id = @RunId)
			THROW 51000, 'Run not found', 1
		IF ISJSON(ISNULL(@Assignments, '[]')) = 0
			THROW 51000, 'The assignments need to be json', 1

		BEGIN TRANSACTION
			UPDATE SolveRuns
				SET [status] = @Status, objective = @Objective, [message] = @Message, infeasibility = @Infeasibility, details = @Details, diagnostics = @Diagnostics,
					unscheduled = @Unscheduled, changes = @Changes, stats = @Stats, finished = SYSUTCDATETIME(), [started] = ISNULL([started], SYSUTCDATETIME())
				WHERE id = @RunId
			DELETE FROM ScheduleAssignments WHERE solveRunId = @RunId
			INSERT INTO ScheduleAssignments (solveRunId, itemKey, itemLabel, segment, resourceKey, poolKey, toolCount, cycles, quantity, startTime, endTime,
					setupMinutes, dueTime, lateMinutes, cost, costReasons, inspections, attributes, [fixed])
				SELECT @RunId, a.itemKey, a.itemLabel, ISNULL(a.segment, 0), a.resourceKey, a.poolKey, ISNULL(a.toolCount, 1), ISNULL(a.cycles, 0), ISNULL(a.quantity, 0),
						a.startTime, a.endTime, ISNULL(a.setupMinutes, 0), a.dueTime, ISNULL(a.lateMinutes, 0), ISNULL(a.cost, 0), a.costReasons, a.inspections, a.attributes,
						ISNULL(a.[fixed], 0)
					FROM OPENJSON(ISNULL(@Assignments, '[]')) WITH (itemKey NVARCHAR(100), itemLabel NVARCHAR(200), segment INT, resourceKey NVARCHAR(100), poolKey NVARCHAR(100),
						toolCount INT, cycles BIGINT, quantity BIGINT, startTime DATETIME2(0), endTime DATETIME2(0), setupMinutes INT, dueTime DATETIME2(0), lateMinutes INT,
						cost INT, costReasons NVARCHAR(MAX) AS JSON, inspections NVARCHAR(MAX) AS JSON, attributes NVARCHAR(MAX) AS JSON, [fixed] BIT) a
		COMMIT TRANSACTION

		SELECT dbo.GetSolveRunJson(@RunId) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[GetSolveRuns] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- A profile's latest runs, newest first, without their assignments
-- EXEC GetSolveRuns '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetSolveRuns]
	@ProfileId VARCHAR(50),
	@Top INT = 50
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetSolveRunJson(r.id) AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY r.requested DESC) + ']'
				FROM (SELECT TOP (ISNULL(@Top, 50)) id, requested FROM SolveRuns (NOLOCK) WHERE profileId = @ProfileId ORDER BY requested DESC) r), '[]') [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[GetSolveRun] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- One run with its assignments, a blank run id gives the published plan, or nothing when there isn't one
-- EXEC GetSolveRun '<profile id>', '<run id>'
CREATE OR ALTER PROCEDURE [dbo].[GetSolveRun]
	@ProfileId VARCHAR(50),
	@RunId VARCHAR(50) = NULL
AS
BEGIN
	BEGIN TRY
		IF NULLIF(@RunId, '') IS NULL
			SET @RunId = (SELECT TOP 1 id FROM SolveRuns (NOLOCK) WHERE profileId = @ProfileId AND published = 1 ORDER BY publishedAt DESC)
		IF @RunId IS NULL
		BEGIN
			SELECT '{}' [results]
			RETURN 1
		END
		IF NOT EXISTS (SELECT 1 FROM SolveRuns (NOLOCK) WHERE id = @RunId AND profileId = @ProfileId)
			THROW 51000, 'Run not found', 1

		SELECT JSON_MODIFY(dbo.GetSolveRunJson(@RunId), '$.assignments', JSON_QUERY(ISNULL(
			(SELECT a.id, a.itemKey, a.itemLabel, a.segment, a.resourceKey, a.poolKey, a.toolCount, a.cycles, a.quantity, a.startTime, a.endTime, a.setupMinutes,
					a.dueTime, a.lateMinutes, a.cost, JSON_QUERY(a.costReasons) [costReasons], JSON_QUERY(a.inspections) [inspections],
					JSON_QUERY(a.attributes) [attributes], a.[fixed]
				FROM ScheduleAssignments a (NOLOCK)
				WHERE a.solveRunId = @RunId
				ORDER BY a.resourceKey, a.startTime
				FOR JSON PATH, INCLUDE_NULL_VALUES), '[]'))) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[PublishSolveRun] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Makes a run the plan, the one before stops being it
-- an emergency run's breakdowns go in the resources' calendars so the next run knows about them too
-- EXEC PublishSolveRun '<profile id>', '<run id>'
CREATE OR ALTER PROCEDURE [dbo].[PublishSolveRun]
	@ProfileId VARCHAR(50),
	@RunId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM SolveRuns (NOLOCK) WHERE id = @RunId AND profileId = @ProfileId)
			THROW 51000, 'Run not found', 1
		IF EXISTS (SELECT 1 FROM SolveRuns (NOLOCK) WHERE id = @RunId AND [status] NOT IN ('Optimal', 'Feasible', 'Stopped'))
			THROW 51000, 'Only a run that found a plan can be published', 1
		IF EXISTS (SELECT 1 FROM SolveRuns (NOLOCK) WHERE id = @RunId AND discarded = 1)
			THROW 51000, 'This run was discarded', 1

		BEGIN TRANSACTION
			UPDATE SolveRuns SET published = 0 WHERE profileId = @ProfileId AND published = 1
			UPDATE SolveRuns SET published = 1, publishedAt = SYSUTCDATETIME() WHERE id = @RunId

			-- each breakdown on the resource's own calendar, made when it hasn't one, taking after the profile's main calendar
			DECLARE @downs TABLE (resourceKey NVARCHAR(100), fromTime DATETIME2(0), toTime DATETIME2(0), reason NVARCHAR(500))
			INSERT INTO @downs (resourceKey, fromTime, toTime, reason)
				SELECT d.resourceKey, d.[from], d.[to], ISNULL(NULLIF(d.reason, ''), 'Emergency stop')
					FROM SolveRuns r (NOLOCK)
						CROSS APPLY OPENJSON(r.settings, '$.downs') WITH (resourceKey NVARCHAR(100), [from] DATETIME2(0), [to] DATETIME2(0), reason NVARCHAR(500)) d
					WHERE r.id = @RunId AND d.resourceKey IS NOT NULL AND d.[to] > d.[from]
			IF EXISTS (SELECT 1 FROM @downs)
			BEGIN
				DECLARE @rootId VARCHAR(50) = (SELECT TOP 1 id FROM Calendars (NOLOCK) WHERE profileId = @ProfileId AND parentCalendarId IS NULL AND resourceKey IS NULL ORDER BY [name])
				DECLARE @resourceTypeId VARCHAR(50) = (SELECT TOP 1 resourceEntityTypeId FROM Calendars (NOLOCK) WHERE profileId = @ProfileId AND resourceEntityTypeId IS NOT NULL)
				INSERT INTO Calendars (profileId, [name], parentCalendarId, resourceEntityTypeId, resourceKey)
					SELECT DISTINCT @ProfileId, d.resourceKey, @rootId, @resourceTypeId, d.resourceKey
						FROM @downs d
						WHERE NOT EXISTS (SELECT 1 FROM Calendars c (NOLOCK) WHERE c.profileId = @ProfileId AND c.resourceKey = d.resourceKey)
				INSERT INTO CalendarExceptions (calendarId, startTime, endTime, kind, reason)
					SELECT c.id, d.fromTime, d.toTime, 'Down', d.reason
						FROM @downs d
							CROSS APPLY (SELECT TOP 1 id FROM Calendars c (NOLOCK) WHERE c.profileId = @ProfileId AND c.resourceKey = d.resourceKey) c
						WHERE NOT EXISTS (SELECT 1 FROM CalendarExceptions x (NOLOCK) WHERE x.calendarId = c.id AND x.startTime = d.fromTime AND x.endTime = d.toTime)
			END
		COMMIT TRANSACTION

		SELECT dbo.GetSolveRunJson(@RunId) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[DiscardSolveRun] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Throws a draft run away, a published run can't be discarded
-- EXEC DiscardSolveRun '<profile id>', '<run id>'
CREATE OR ALTER PROCEDURE [dbo].[DiscardSolveRun]
	@ProfileId VARCHAR(50),
	@RunId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF EXISTS (SELECT 1 FROM SolveRuns (NOLOCK) WHERE id = @RunId AND profileId = @ProfileId AND published = 1)
			THROW 51000, 'The published plan can''t be discarded, publish another run instead', 1
		UPDATE SolveRuns SET discarded = 1 WHERE id = @RunId AND profileId = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Run not found', 1

		SELECT dbo.GetSolveRunJson(@RunId) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[GetPlanLocks] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The locks on a profile's plan
-- EXEC GetPlanLocks '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetPlanLocks]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL((SELECT l.id, l.kind, l.lockedUntil, l.resourceKey, l.itemKey, l.reason, l.created
			FROM PlanLocks l (NOLOCK)
			WHERE l.profileId = @ProfileId
			ORDER BY l.kind DESC, l.lockedUntil, l.itemKey
			FOR JSON PATH, INCLUDE_NULL_VALUES), '[]') [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[SavePlanLock] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a lock, Window locks what starts before lockedUntil (on one resource or all), Item locks one item where it is
-- a window on the same resource replaces the one there was
-- EXEC SavePlanLock '<profile id>', 'Window', '2026-11-12T06:00', NULL, NULL, 'Tomorrow is fixed'
CREATE OR ALTER PROCEDURE [dbo].[SavePlanLock]
	@ProfileId VARCHAR(50),
	@Kind VARCHAR(10),
	@LockedUntil DATETIME2(0) = NULL,
	@ResourceKey NVARCHAR(100) = NULL,
	@ItemKey NVARCHAR(100) = NULL,
	@Reason NVARCHAR(500) = NULL
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1
		IF ISNULL(@Kind, '') NOT IN ('Window', 'Item')
			THROW 51000, 'The kind needs to be Window or Item', 1
		IF @Kind = 'Window' AND @LockedUntil IS NULL
			THROW 51000, 'A window lock needs the time it locks until', 1
		IF @Kind = 'Item' AND NULLIF(@ItemKey, '') IS NULL
			THROW 51000, 'An item lock needs the item', 1

		BEGIN TRANSACTION
			IF @Kind = 'Window'
				DELETE FROM PlanLocks WHERE profileId = @ProfileId AND kind = 'Window' AND ISNULL(resourceKey, '') = ISNULL(@ResourceKey, '')
			ELSE
				DELETE FROM PlanLocks WHERE profileId = @ProfileId AND kind = 'Item' AND itemKey = @ItemKey
			INSERT INTO PlanLocks (profileId, kind, lockedUntil, resourceKey, itemKey, reason)
				VALUES (@ProfileId, @Kind, CASE WHEN @Kind = 'Window' THEN @LockedUntil END, NULLIF(@ResourceKey, ''), CASE WHEN @Kind = 'Item' THEN @ItemKey END, NULLIF(@Reason, ''))
		COMMIT TRANSACTION

		EXEC GetPlanLocks @ProfileId
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[DeletePlanLock] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes a lock
-- EXEC DeletePlanLock '<profile id>', '<lock id>'
CREATE OR ALTER PROCEDURE [dbo].[DeletePlanLock]
	@ProfileId VARCHAR(50),
	@LockId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		DELETE FROM PlanLocks WHERE id = @LockId AND profileId = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Lock not found', 1

		EXEC GetPlanLocks @ProfileId
		RETURN 1
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO
