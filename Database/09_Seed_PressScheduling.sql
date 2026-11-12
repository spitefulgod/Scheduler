/****** Object:  The Press Scheduling demo profile ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The reference profile, set up on the demo data in 08_DemoSource.sql
-- Jobs moulded on presses with mould tools of different sizes and copies, compounds that can't mix, cells, inspections and splitting
-- Only added when there's no profile called Press Scheduling, so changes made to it are never overwritten
-- the first version of this profile (from before the rule engine) is replaced, it can't be run

IF EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE [name] = 'Press Scheduling' AND [description] = 'Reference profile: jobs on presses, consuming mould tools, with compound rules.')
BEGIN
	DECLARE @oldId VARCHAR(50) = (SELECT id FROM Profiles WHERE [name] = 'Press Scheduling')
	DELETE FROM Rules WHERE profileId = @oldId
	DELETE FROM Relationships WHERE profileId = @oldId
	DELETE c FROM CalendarExceptions c JOIN Calendars k ON k.id = c.calendarId WHERE k.profileId = @oldId
	UPDATE Calendars SET parentCalendarId = NULL WHERE profileId = @oldId
	DELETE FROM Calendars WHERE profileId = @oldId
	UPDATE Attributes SET referencesEntityTypeId = NULL WHERE entityTypeId IN (SELECT id FROM EntityTypes WHERE profileId = @oldId)
	DELETE FROM EntityTypes WHERE profileId = @oldId
	DELETE FROM Profiles WHERE id = @oldId
	PRINT 'The first Press Scheduling profile was replaced'
END
GO

IF EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE [name] = 'Press Scheduling')
BEGIN
	PRINT 'Press Scheduling is already there'
	RETURN
END

SET XACT_ABORT ON
BEGIN TRANSACTION

DECLARE @profileId VARCHAR(50) = CAST(NEWID() AS VARCHAR(50)), @connectionId VARCHAR(50)
DECLARE @today DATETIME2(0) = CAST(CAST(GETDATE() AS DATE) AS DATETIME2(0))

-- The demo connection, shared with any other profile that wants the demo data
SELECT @connectionId = id FROM Connections WHERE typeId = 'DEMO'
IF @connectionId IS NULL
BEGIN
	SET @connectionId = CAST(NEWID() AS VARCHAR(50))
	INSERT INTO Connections (id, [name], typeId, settings, lastTested, lastTestOk, lastTestMessage)
		VALUES (@connectionId, 'Demo source (SchedulerMeta.src)', 'DEMO', '{}', SYSUTCDATETIME(), 1, 'Demo data in this database')
END

INSERT INTO Profiles (id, [name], [description], timeUnitMinutes, settings)
	VALUES (@profileId, 'Press Scheduling', 'Moulding: jobs on presses with multi copy, multi cavity tools of different sizes, compounds that can''t mix, cells, inspections and splitting.', 15,
		'{"horizonDays":21,"timeLimitSeconds":30,"allowUnscheduled":true,"colourBy":"compound","labelBy":"job_no","lockHours":0}')

-- Sources, one per demo table, with the columns read from the database
DECLARE @sources TABLE ([key] VARCHAR(20), id VARCHAR(50), [name] NVARCHAR(200), kind VARCHAR(10), objectName NVARCHAR(128))
INSERT INTO @sources ([key], id, [name], kind, objectName) VALUES
	('jobs', CAST(NEWID() AS VARCHAR(50)), 'Jobs', 'table', 'Job'),
	('presses', CAST(NEWID() AS VARCHAR(50)), 'Presses', 'table', 'Press'),
	('tools', CAST(NEWID() AS VARCHAR(50)), 'Tools', 'table', 'Tool'),
	('compounds', CAST(NEWID() AS VARCHAR(50)), 'Compounds', 'table', 'Compound'),
	('sizeClasses', CAST(NEWID() AS VARCHAR(50)), 'Tool size classes', 'table', 'ToolSizeClass'),
	('jobAltTools', CAST(NEWID() AS VARCHAR(50)), 'Job alternative tools', 'table', 'JobAlternativeTool'),
	('compoundPress', CAST(NEWID() AS VARCHAR(50)), 'Compound press grades', 'table', 'CompoundPress'),
	('compoundCompat', CAST(NEWID() AS VARCHAR(50)), 'Compound compatibility', 'table', 'CompoundCompatibility'),
	('toolPressFit', CAST(NEWID() AS VARCHAR(50)), 'Tool press fit', 'view', 'ToolPressFit'),
	('pressSizeSlots', CAST(NEWID() AS VARCHAR(50)), 'Press size slots', 'table', 'PressSizeSlots')

INSERT INTO Sources (id, profileId, connectionId, [name], kind, [schema], objectName, [columns], columnsRead)
	SELECT s.id, @profileId, @connectionId, s.[name], s.kind, 'src', s.objectName,
		ISNULL((SELECT c.COLUMN_NAME [name], c.DATA_TYPE [sqlType], CAST(CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS BIT) [nullable]
			FROM INFORMATION_SCHEMA.COLUMNS c
			WHERE c.TABLE_SCHEMA = 'src' AND c.TABLE_NAME = s.objectName
			ORDER BY c.ORDINAL_POSITION
			FOR JSON PATH), '[]'),
		SYSUTCDATETIME()
		FROM @sources s

-- Entity types and their attributes, the job's cycle time and quantity drive its length through the tool's cavities and copies
DECLARE @entities TABLE ([key] VARCHAR(20), id VARCHAR(50), [name] NVARCHAR(100), kind VARCHAR(20), sourceKey VARCHAR(20), [description] NVARCHAR(2000))
INSERT INTO @entities ([key], id, [name], kind, sourceKey, [description]) VALUES
	('job', CAST(NEWID() AS VARCHAR(50)), 'Job', 'Item', 'jobs', 'Jobs to mould, a quantity of parts at a cycle time'),
	('press', CAST(NEWID() AS VARCHAR(50)), 'Press', 'Resource', 'presses', 'Presses the tools are fitted in'),
	('tool', CAST(NEWID() AS VARCHAR(50)), 'Tool', 'Resource', 'tools', 'Mould tools, with copies, cavities and a size'),
	('compound', CAST(NEWID() AS VARCHAR(50)), 'Compound', 'Reference', 'compounds', 'Rubber compounds'),
	('sizeClass', CAST(NEWID() AS VARCHAR(50)), 'Size class', 'Reference', 'sizeClasses', 'Tool size classes for the platen configurations')

INSERT INTO EntityTypes (id, profileId, [name], kind, sourceId, [description])
	SELECT e.id, @profileId, e.[name], e.kind, s.id, e.[description]
		FROM @entities e
			JOIN @sources s ON s.[key] = e.sourceKey

INSERT INTO Attributes (entityTypeId, [name], dataType, [role], sourceColumn, referencesEntityTypeId, sortOrder)
	SELECT e.id, a.[name], a.dataType, a.[role], a.sourceColumn, r.id, a.sortOrder
		FROM (VALUES
			('compound', 'code', 'STRING', 'Id', 'CompoundCode', NULL, 1),
			('compound', 'description', 'STRING', 'Name', 'Description', NULL, 2),
			('compound', 'family', 'STRING', 'Category', 'Family', NULL, 3),
			('compound', 'cure_temp_c', 'INTEGER', 'Custom', 'CureTempC', NULL, 4),
			('sizeClass', 'code', 'STRING', 'Id', 'SizeClass', NULL, 1),
			('sizeClass', 'description', 'STRING', 'Name', 'Description', NULL, 2),
			('press', 'code', 'STRING', 'Id', 'PressCode', NULL, 1),
			('press', 'description', 'STRING', 'Name', 'Description', NULL, 2),
			('press', 'platen_slots', 'INTEGER', 'Capacity', 'PlatenSlots', NULL, 3),
			('press', 'max_tool_size', 'INTEGER', 'Size', 'MaxToolSize', NULL, 4),
			('press', 'platen_space', 'INTEGER', 'Custom', 'PlatenSpace', NULL, 5),
			('press', 'tonnage', 'INTEGER', 'Custom', 'Tonnage', NULL, 6),
			('press', 'cell', 'STRING', 'Category', 'Cell', NULL, 7),
			('tool', 'code', 'STRING', 'Id', 'ToolCode', NULL, 1),
			('tool', 'description', 'STRING', 'Name', 'Description', NULL, 2),
			('tool', 'size', 'INTEGER', 'Size', 'Size', NULL, 3),
			('tool', 'size_class', 'STRING', 'Category', 'SizeClass', 'sizeClass', 4),
			('tool', 'copies', 'INTEGER', 'Copies', 'Copies', NULL, 5),
			('tool', 'cavities', 'INTEGER', 'Custom', 'Cavities', NULL, 6),
			('tool', 'change_minutes', 'INTEGER', 'Custom', 'ChangeMinutes', NULL, 7),
			('job', 'job_no', 'STRING', 'Id', 'JobNo', NULL, 1),
			('job', 'compound', 'STRING', 'Category', 'CompoundCode', 'compound', 2),
			('job', 'tool', 'STRING', 'Custom', 'ToolCode', 'tool', 3),
			('job', 'qty', 'INTEGER', 'Quantity', 'Quantity', NULL, 4),
			('job', 'cycle_secs', 'INTEGER', 'CycleTime', 'CycleSecs', NULL, 5),
			('job', 'release', 'DATETIME', 'ReleaseDate', 'ReleaseDate', NULL, 6),
			('job', 'due', 'DATETIME', 'DueDate', 'DueDate', NULL, 7),
			('job', 'priority', 'INTEGER', 'Priority', 'Priority', NULL, 8),
			('job', 'cell', 'STRING', 'Category', 'Cell', NULL, 9),
			('job', 'customer', 'STRING', 'Category', 'Customer', NULL, 10),
			('job', 'inspect_every', 'INTEGER', 'Custom', 'InspectEvery', NULL, 11),
			('job', 'allow_parallel', 'BOOLEAN', 'Custom', 'AllowParallel', NULL, 12)
		) a (entityKey, [name], dataType, [role], sourceColumn, referencesKey, sortOrder)
			JOIN @entities e ON e.[key] = a.entityKey
			LEFT JOIN @entities r ON r.[key] = a.referencesKey

-- Relationships, read live from the demo tables, a value typed into the matrix overrides the source's
DECLARE @relationships TABLE ([key] VARCHAR(20), id VARCHAR(50))
INSERT INTO @relationships ([key], id) VALUES
	('jobAlt', CAST(NEWID() AS VARCHAR(50))),
	('toolPress', CAST(NEWID() AS VARCHAR(50))),
	('compPress', CAST(NEWID() AS VARCHAR(50))),
	('compCompat', CAST(NEWID() AS VARCHAR(50))),
	('sizeSlots', CAST(NEWID() AS VARCHAR(50)))

INSERT INTO Relationships (id, profileId, [name], entityTypeAId, entityTypeBId, valueScaleId, defaultValue, [symmetric], editor, drives,
		sourceId, sourceAColumn, sourceBColumn, sourceValueColumn)
	SELECT r.id, @profileId, d.[name], a.id, b.id, d.valueScaleId, d.defaultValue, d.[symmetric], d.editor, d.drives,
			s.id, d.sourceAColumn, d.sourceBColumn, d.sourceValueColumn
		FROM (VALUES
			('jobAlt', 'Job alternative tools', 'job', 'tool', 'BOOLEAN', NULL, 0, 'List', 'UsesTooling', 'jobAltTools', 'JobNo', 'ToolCode', NULL),
			('toolPress', 'Tool fits press', 'tool', 'press', 'BOOLEAN', 'N', 0, 'Matrix', 'Allowed', 'toolPressFit', 'ToolCode', 'PressCode', 'Fits'),
			('compPress', 'Compound on press', 'compound', 'press', 'GRADED', 'A', 0, 'Matrix', 'Allowed', 'compoundPress', 'CompoundCode', 'PressCode', 'Grade'),
			('compCompat', 'Compound compatibility', 'compound', 'compound', 'BOOLEAN', 'Y', 1, 'Matrix', 'Compatible', 'compoundCompat',
				'CompoundA', 'CompoundB', 'Compatible'),
			('sizeSlots', 'Press size slots', 'press', 'sizeClass', 'NUMBER', NULL, 0, 'Matrix', 'CapacityMatrix', 'pressSizeSlots', 'PressCode', 'SizeClass', 'Slots')
		) d ([key], [name], aKey, bKey, valueScaleId, defaultValue, [symmetric], editor, drives, sourceKey, sourceAColumn, sourceBColumn, sourceValueColumn)
			JOIN @relationships r ON r.[key] = d.[key]
			JOIN @entities a ON a.[key] = d.aKey
			JOIN @entities b ON b.[key] = d.bKey
			JOIN @sources s ON s.[key] = d.sourceKey

DECLARE @jobAltId VARCHAR(50) = (SELECT id FROM @relationships WHERE [key] = 'jobAlt'),
	@toolPressId VARCHAR(50) = (SELECT id FROM @relationships WHERE [key] = 'toolPress'),
	@compPressId VARCHAR(50) = (SELECT id FROM @relationships WHERE [key] = 'compPress'),
	@compCompatId VARCHAR(50) = (SELECT id FROM @relationships WHERE [key] = 'compCompat'),
	@sizeSlotsId VARCHAR(50) = (SELECT id FROM @relationships WHERE [key] = 'sizeSlots')

-- One typed in override, to show it beats the source: FKM is preferred on P08
INSERT INTO RelationshipValues (relationshipId, aKey, bKey, [value]) VALUES (@compPressId, N'FKM-75', N'P08', N'P')

-- Calendars, a site calendar every press takes on with a shutdown in two weeks, and one per press
DECLARE @pressTypeId VARCHAR(50) = (SELECT id FROM @entities WHERE [key] = 'press'), @siteId VARCHAR(50) = CAST(NEWID() AS VARCHAR(50))
INSERT INTO Calendars (id, profileId, [name]) VALUES (@siteId, @profileId, 'Site')

-- 06:00 Monday to 06:00 Saturday round the clock, then 06:00 to 14:00 on Saturday
INSERT INTO CalendarShifts (calendarId, [dayOfWeek], startTime, endTime)
	SELECT @siteId, d.[day], '06:00', CASE WHEN d.[day] = 6 THEN '14:00' ELSE '06:00' END
		FROM (VALUES (1), (2), (3), (4), (5), (6)) d ([day])
INSERT INTO CalendarExceptions (calendarId, startTime, endTime, kind, reason)
	VALUES (@siteId, DATEADD(DAY, 14, @today), DATEADD(DAY, 16, @today), 'Down', 'Site shutdown')

INSERT INTO Calendars (profileId, [name], parentCalendarId, resourceEntityTypeId, resourceKey)
	SELECT @profileId, 'Press ' + p.PressCode, @siteId, @pressTypeId, p.PressCode
		FROM src.Press p
INSERT INTO CalendarExceptions (calendarId, startTime, endTime, kind, reason)
	SELECT c.id, DATEADD(HOUR, e.fromHours, @today), DATEADD(HOUR, e.toHours, @today), 'Down', e.reason
		FROM (VALUES
			('P03', 54, 84, 'Planned maintenance'),
			('P09', 0, 48, 'Commissioning')
		) e (pressCode, fromHours, toHours, reason)
			JOIN Calendars c ON c.profileId = @profileId AND c.resourceKey = e.pressCode

-- Rules, blocks is what the rule builder makes: who it's for and the block's parameters
INSERT INTO Rules (profileId, [name], ruleTypeId, strength, [weight], blocks, sortOrder)
	SELECT @profileId, r.[name], r.ruleTypeId, r.strength, r.[weight], r.blocks, r.sortOrder
		FROM (VALUES
			('Jobs run on presses', 'RunsOn', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"resource":"Press"}}', 10),
			('Jobs use their tool, or an alternative, as many copies as fit', 'UsesTooling', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"tooling":"Tool","toolAttribute":"item.tool","alternativesRelationshipId":"' + @jobAltId + '","minCount":"1","maxCount":"tooling.copies","unitsPerCycle":"tooling.cavities"}}', 20),
			('Tools only go in presses they fit', 'Allowed', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"relationshipId":"' + @toolPressId + '","a":"tooling","b":"resource"}}', 30),
			('Compounds only run on suitable presses', 'Allowed', 'Hard', 1,
				'{"who":{"entity":"Job"},"params":{"relationshipId":"' + @compPressId + '","a":"item.compound","b":"resource"}}', 40),
			('Platen configuration by tool size', 'CapacityMatrix', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"relationshipId":"' + @sizeSlotsId + '","key":"tooling.size_class","per":"copy"}}', 50),
			('Platen space for the tools fitted', 'Capacity', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"capacity":"resource.platen_space","demand":"tooling.size","per":"copy"}}', 60),
			('Only compatible compounds share a press', 'Compatible', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"relationshipId":"' + @compCompatId + '","attribute":"item.compound"}}', 70),
			('Jobs sharing a press run the same cycle (within a minute)', 'MustMatch', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"attribute":"item.cycle_secs","tolerance":"60"}}', 80),
			('Jobs stay in their cell, moving one costs', 'Condition', 'Soft', 200,
				'{"who":{"entity":"Job"},"params":{"condition":"item.cell = resource.cell"}}', 90),
			('Tool change time', 'Setup', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"minutes":"tooling.change_minutes"}}', 100),
			('First off inspection and every N cycles', 'Inspection', 'Hard', 0,
				'{"who":{"entity":"Job"},"params":{"firstOffMinutes":"15","everyCycles":"item.inspect_every","everyMinutes":"5","setupMinutes":"10"}}', 110),
			('Big jobs may split, in parallel when the job allows', 'Split', 'Hard', 0,
				'{"who":{"entity":"Job","when":"item.qty >= 900"},"params":{"maxSegments":"2","minCycles":"20","allowParallel":"item.allow_parallel","segmentPenalty":"40"}}', 120),
			('At most two silicone jobs at once', 'SharedLimit', 'Hard', 0,
				'{"who":{"entity":"Job","when":"item.compound = ''SIL-50''"},"params":{"capacity":"2"}}', 130),
			('Presses only run on their calendar', 'Calendar', 'Hard', 0,
				'{"params":{"applyTo":"resource"}}', 140),
			('On time, priority 1 first', 'Goal', 'Soft', 10,
				'{"who":{"entity":"Job"},"params":{"goal":"Lateness","weightAttribute":"item.priority","higherIsMoreImportant":"false","maxValue":"5"}}', 150),
			('Prefer preferred presses', 'Goal', 'Soft', 1,
				'{"params":{"goal":"Preferences"}}', 160),
			('Keep the published plan steady', 'Goal', 'Soft', 20,
				'{"params":{"goal":"Stability","timeWeight":"1"}}', 170)
		) r ([name], ruleTypeId, strength, [weight], blocks, sortOrder)

COMMIT TRANSACTION
GO

-- Platen combinations, added to a Press Scheduling profile that hasn't got them, including one made before they were in the demo:
-- a Configuration entity, a combination x size class matrix and a press x combination list read from the demo tables, and a rule
-- that keeps each press in one combination a day, 90 minutes to change, in place of the per size limits
DECLARE @profileId VARCHAR(50) = (SELECT id FROM Profiles (NOLOCK) WHERE [name] = 'Press Scheduling')
IF @profileId IS NULL OR EXISTS (SELECT 1 FROM EntityTypes (NOLOCK) WHERE profileId = @profileId AND [name] = 'Configuration')
	RETURN
IF OBJECT_ID(N'src.PressConfiguration', N'U') IS NULL
BEGIN
	PRINT 'The demo combination tables are missing, run 08_DemoSource.sql first'
	RETURN
END

SET XACT_ABORT ON
BEGIN TRANSACTION

DECLARE @connectionId VARCHAR(50) = (SELECT TOP 1 connectionId FROM Sources (NOLOCK) WHERE profileId = @profileId)
DECLARE @pressTypeId VARCHAR(50) = (SELECT id FROM EntityTypes (NOLOCK) WHERE profileId = @profileId AND [name] = 'Press'),
	@sizeClassTypeId VARCHAR(50) = (SELECT id FROM EntityTypes (NOLOCK) WHERE profileId = @profileId AND [name] = 'Size class')
IF @pressTypeId IS NULL OR @sizeClassTypeId IS NULL
BEGIN
	ROLLBACK TRANSACTION
	PRINT 'Press Scheduling has no Press or Size class entity, platen combinations not added'
	RETURN
END

DECLARE @sources TABLE ([key] VARCHAR(20), id VARCHAR(50), [name] NVARCHAR(200), kind VARCHAR(10), objectName NVARCHAR(128))
INSERT INTO @sources ([key], id, [name], kind, objectName) VALUES
	('configs', CAST(NEWID() AS VARCHAR(50)), 'Platen combinations', 'table', 'PressConfiguration'),
	('configSizes', CAST(NEWID() AS VARCHAR(50)), 'Platen combination sizes', 'table', 'PressConfigurationSize'),
	('configPress', CAST(NEWID() AS VARCHAR(50)), 'Press combinations', 'view', 'PressConfigurationAllowed')

INSERT INTO Sources (id, profileId, connectionId, [name], kind, [schema], objectName, [columns], columnsRead)
	SELECT s.id, @profileId, @connectionId, s.[name], s.kind, 'src', s.objectName,
		ISNULL((SELECT c.COLUMN_NAME [name], c.DATA_TYPE [sqlType], CAST(CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS BIT) [nullable]
			FROM INFORMATION_SCHEMA.COLUMNS c
			WHERE c.TABLE_SCHEMA = 'src' AND c.TABLE_NAME = s.objectName
			ORDER BY c.ORDINAL_POSITION
			FOR JSON PATH), '[]'),
		SYSUTCDATETIME()
		FROM @sources s

DECLARE @configTypeId VARCHAR(50) = CAST(NEWID() AS VARCHAR(50))
INSERT INTO EntityTypes (id, profileId, [name], kind, sourceId, [description])
	SELECT @configTypeId, @profileId, 'Configuration', 'Reference', s.id, 'Platen combinations, how many tools of each size class a press holds at once'
		FROM @sources s
		WHERE s.[key] = 'configs'
INSERT INTO Attributes (entityTypeId, [name], dataType, [role], sourceColumn, sortOrder) VALUES
	(@configTypeId, 'code', 'STRING', 'Id', 'ConfigCode', 1),
	(@configTypeId, 'description', 'STRING', 'Name', 'Description', 2),
	(@configTypeId, 'platen_slots', 'INTEGER', 'Capacity', 'PlatenSlots', 3)

DECLARE @configSizesId VARCHAR(50) = CAST(NEWID() AS VARCHAR(50)), @configPressId VARCHAR(50) = CAST(NEWID() AS VARCHAR(50))
INSERT INTO Relationships (id, profileId, [name], entityTypeAId, entityTypeBId, valueScaleId, defaultValue, [symmetric], editor, drives,
		sourceId, sourceAColumn, sourceBColumn, sourceValueColumn)
	SELECT d.id, @profileId, d.[name], d.aId, d.bId, d.valueScaleId, NULL, 0, d.editor, 'Configurations', s.id, d.aColumn, d.bColumn, d.valueColumn
		FROM (VALUES
			(@configSizesId, 'Platen combinations', @configTypeId, @sizeClassTypeId, 'NUMBER', 'Matrix', 'configSizes', 'ConfigCode', 'SizeClass', 'Tools'),
			(@configPressId, 'Press combinations', @pressTypeId, @configTypeId, 'BOOLEAN', 'List', 'configPress', 'PressCode', 'ConfigCode', NULL)
		) d (id, [name], aId, bId, valueScaleId, editor, sourceKey, aColumn, bColumn, valueColumn)
			JOIN @sources s ON s.[key] = d.sourceKey

-- the per size limits can hold 6 small and 2 medium at once, which no combination does, so that rule is switched off
UPDATE Rules SET [enabled] = 0, modified = SYSUTCDATETIME() WHERE profileId = @profileId AND ruleTypeId = 'CapacityMatrix'
INSERT INTO Rules (profileId, [name], ruleTypeId, strength, [weight], blocks, sortOrder)
	VALUES (@profileId, 'Platen combinations, one a day, 90 minutes to change', 'Configurations', 'Hard', 0,
		'{"who":{"entity":"Job"},"params":{"relationshipId":"' + @configSizesId + '","allowedRelationshipId":"' + @configPressId + '","key":"tooling.size_class","per":"copy","windowHours":"24","changeMinutes":"90","changeCost":"20"}}', 55)

COMMIT TRANSACTION
PRINT 'Platen combinations added to Press Scheduling'
GO
