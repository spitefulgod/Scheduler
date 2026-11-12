/****** Object:  StoredProcedures for profiles ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  StoredProcedure [dbo].[GetProfiles] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Every profile, by name
-- EXEC GetProfiles
CREATE OR ALTER PROCEDURE [dbo].[GetProfiles]
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetProfileJson(p.id) AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY p.name) + ']'
				FROM Profiles p (NOLOCK)), '[]') [results]

		RETURN 1
	END TRY
	BEGIN CATCH
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[GetProfile] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- One profile
-- EXEC GetProfile '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetProfile]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1

		SELECT dbo.GetProfileJson(@ProfileId) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[SaveProfile] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a profile, or changes one when @ProfileId is given
-- EXEC SaveProfile NULL, 'Press Scheduling', 'Jobs on presses', 15
CREATE OR ALTER PROCEDURE [dbo].[SaveProfile]
	@ProfileId VARCHAR(50) = NULL,
	@Name NVARCHAR(200),
	@Description NVARCHAR(2000) = NULL,
	@TimeUnitMinutes INT = 15
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF @Name = ''
			THROW 51000, 'A profile needs a name', 1
		IF ISNULL(@TimeUnitMinutes, 0) NOT BETWEEN 1 AND 1440
			THROW 51000, 'The time unit needs to be between 1 and 1440 minutes', 1
		IF EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE [name] = @Name AND id <> ISNULL(@ProfileId, ''))
			THROW 51000, 'Another profile already has this name', 1

		IF @ProfileId IS NULL
		BEGIN
			SET @ProfileId = CAST(NEWID() AS VARCHAR(50))
			INSERT INTO Profiles (id, [name], [description], timeUnitMinutes)
				VALUES (@ProfileId, @Name, NULLIF(LTRIM(RTRIM(@Description)), ''), @TimeUnitMinutes)
		END
		ELSE
		BEGIN
			UPDATE Profiles
				SET [name] = @Name, [description] = NULLIF(LTRIM(RTRIM(@Description)), ''), timeUnitMinutes = @TimeUnitMinutes, modified = SYSUTCDATETIME()
				WHERE id = @ProfileId
			IF @@ROWCOUNT = 0
				THROW 51000, 'Profile not found', 1
		END

		SELECT dbo.GetProfileJson(@ProfileId) [results]
		RETURN 1
	END TRY
	BEGIN CATCH
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO

/****** Object:  StoredProcedure [dbo].[GetNavigator] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The navigator tree for a profile, a folder each for connections, sources, entities, relationships, calendars, rules and runs
-- every node is { id, kind, label, icon, itemId, badge, properties, children }, properties are what the properties panel shows
-- connections are shared by every profile
-- EXEC GetNavigator '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetNavigator]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1

		DECLARE @connections NVARCHAR(MAX) = (
			SELECT 'connection:' + c.id [id], 'connection' [kind], c.name [label], 'database' [icon], c.id [itemId],
				CASE c.lastTestOk WHEN 1 THEN 'ok' WHEN 0 THEN 'error' END [badge],
				JSON_QUERY((SELECT t.name [Type],
					CASE WHEN c.typeId = 'DEMO' THEN '(SchedulerMeta)' ELSE JSON_VALUE(c.settings, '$.server') END [Server],
					CASE WHEN c.typeId = 'DEMO' THEN '(SchedulerMeta)' ELSE JSON_VALUE(c.settings, '$.database') END [Database],
					CASE WHEN c.typeId = 'DEMO' OR ISNULL(JSON_VALUE(c.settings, '$.user'), '') = '' THEN 'Windows' ELSE JSON_VALUE(c.settings, '$.user') END [Sign in],
					c.lastTestMessage [Last test]
					FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties]
				FROM Connections c (NOLOCK)
					JOIN ConnectionTypes t (NOLOCK) ON t.id = c.typeId
				ORDER BY c.name
				FOR JSON PATH, INCLUDE_NULL_VALUES)

		DECLARE @sources NVARCHAR(MAX) = (
			SELECT 'source:' + s.id [id], 'source' [kind], s.name [label], CASE s.kind WHEN 'query' THEN 'code' WHEN 'view' THEN 'visibility' ELSE 'table' END [icon],
				s.id [itemId], NULL [badge],
				JSON_QUERY((SELECT s.kind [Kind], CASE WHEN s.kind = 'query' THEN '(saved select)' ELSE s.[schema] + '.' + s.objectName END [Object],
					c.name [Connection], (SELECT COUNT(*) FROM OPENJSON(s.[columns])) [Columns], s.columnsRead [Columns read]
					FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties]
				FROM Sources s (NOLOCK)
					JOIN Connections c (NOLOCK) ON c.id = s.connectionId
				WHERE s.profileId = @ProfileId
				ORDER BY s.name
				FOR JSON PATH, INCLUDE_NULL_VALUES)

		DECLARE @entities NVARCHAR(MAX) = (
			SELECT 'entity:' + e.id [id], 'entity' [kind], e.name [label],
				CASE e.kind WHEN 'Item' THEN 'deployed_code' WHEN 'Resource' THEN 'precision_manufacturing' ELSE 'label' END [icon],
				e.id [itemId], e.kind [badge],
				JSON_QUERY((SELECT e.kind [Kind], s.name [Source], e.durationFormula [Duration formula],
					(SELECT COUNT(*) FROM Attributes a (NOLOCK) WHERE a.entityTypeId = e.id) [Attributes]
					FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties],
				JSON_QUERY((SELECT 'attribute:' + a.id [id], 'attribute' [kind], a.name [label], 'data_object' [icon], a.id [itemId],
					NULLIF(a.[role], 'Custom') [badge],
					JSON_QUERY((SELECT a.dataType [Data type], a.[role] [Role], a.sourceColumn [Source column], r.name [References]
						FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties]
					FROM Attributes a (NOLOCK)
						LEFT JOIN EntityTypes r (NOLOCK) ON r.id = a.referencesEntityTypeId
					WHERE a.entityTypeId = e.id
					ORDER BY a.sortOrder, a.name
					FOR JSON PATH, INCLUDE_NULL_VALUES)) [children]
				FROM EntityTypes e (NOLOCK)
					LEFT JOIN Sources s (NOLOCK) ON s.id = e.sourceId
				WHERE e.profileId = @ProfileId
				ORDER BY CASE e.kind WHEN 'Item' THEN 1 WHEN 'Resource' THEN 2 ELSE 3 END, e.name
				FOR JSON PATH, INCLUDE_NULL_VALUES)

		DECLARE @relationships NVARCHAR(MAX) = (
			SELECT 'relationship:' + r.id [id], 'relationship' [kind], r.name [label], CASE r.editor WHEN 'List' THEN 'list' ELSE 'grid_on' END [icon],
				r.id [itemId], CAST(v.total AS VARCHAR(20)) [badge],
				JSON_QUERY((SELECT a.name [A], b.name [B], vs.name [Scale], r.defaultValue [Default], r.[symmetric] [Symmetric], r.editor [Editor],
					r.drives [Drives], v.total [Values]
					FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties]
				FROM Relationships r (NOLOCK)
					JOIN EntityTypes a (NOLOCK) ON a.id = r.entityTypeAId
					JOIN EntityTypes b (NOLOCK) ON b.id = r.entityTypeBId
					JOIN ValueScales vs (NOLOCK) ON vs.id = r.valueScaleId
					CROSS APPLY (SELECT COUNT(*) total FROM RelationshipValues x (NOLOCK) WHERE x.relationshipId = r.id) v
				WHERE r.profileId = @ProfileId
				ORDER BY r.name
				FOR JSON PATH, INCLUDE_NULL_VALUES)

		DECLARE @calendars NVARCHAR(MAX) = (
			SELECT 'calendar:' + c.id [id], 'calendar' [kind], c.name [label], 'calendar_today' [icon], c.id [itemId],
				CAST(NULLIF(x.total, 0) AS VARCHAR(20)) [badge],
				JSON_QUERY((SELECT p.name [Inherits], c.resourceKey [Resource], x.total [Exceptions]
					FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties]
				FROM Calendars c (NOLOCK)
					LEFT JOIN Calendars p (NOLOCK) ON p.id = c.parentCalendarId
					CROSS APPLY (SELECT COUNT(*) total FROM CalendarExceptions e (NOLOCK) WHERE e.calendarId = c.id) x
				WHERE c.profileId = @ProfileId
				ORDER BY CASE WHEN c.parentCalendarId IS NULL THEN 0 ELSE 1 END, c.name
				FOR JSON PATH, INCLUDE_NULL_VALUES)

		DECLARE @rules NVARCHAR(MAX) = (
			SELECT 'rule:' + r.id [id], 'rule' [kind], r.name [label], CASE WHEN r.enabled = 1 THEN 'rule' ELSE 'block' END [icon], r.id [itemId],
				CASE WHEN r.strength = 'Hard' THEN 'Must' ELSE 'Should ' + CAST(r.weight AS VARCHAR(10)) END [badge],
				JSON_QUERY((SELECT t.name [Block], r.strength [Strength], r.weight [Weight], r.enabled [Enabled]
					FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties]
				FROM Rules r (NOLOCK)
					JOIN RuleTypes t (NOLOCK) ON t.id = r.ruleTypeId
				WHERE r.profileId = @ProfileId
				ORDER BY r.sortOrder, r.name
				FOR JSON PATH, INCLUDE_NULL_VALUES)

		DECLARE @runs NVARCHAR(MAX) = (
			SELECT TOP 20 'run:' + r.id [id], 'run' [kind],
				CASE r.kind WHEN 'WhatIf' THEN 'What if ' WHEN 'Emergency' THEN 'Emergency ' ELSE 'Run ' END + CONVERT(VARCHAR(16), r.requested, 120) [label],
				CASE WHEN r.published = 1 THEN 'verified' WHEN r.kind = 'Emergency' THEN 'emergency' WHEN r.kind = 'WhatIf' THEN 'alt_route' ELSE 'play_circle' END [icon],
				r.id [itemId], CASE WHEN r.published = 1 THEN 'Plan' WHEN r.discarded = 1 THEN 'Discarded' ELSE r.[status] END [badge],
				JSON_QUERY((SELECT r.kind [Kind], r.[status] [Status], r.requested [Requested], r.objective [Objective], r.published [Published], r.[message] [Message]
					FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) [properties]
				FROM SolveRuns r (NOLOCK)
				WHERE r.profileId = @ProfileId AND (r.discarded = 0 OR r.published = 1)
				ORDER BY r.requested DESC
				FOR JSON PATH, INCLUDE_NULL_VALUES)

		SELECT (
			SELECT f.id, 'folder' [kind], f.label, f.icon, JSON_QUERY(ISNULL(f.children, '[]')) [children]
				FROM (VALUES
					(1, 'connections', 'Connections', 'lan', @connections),
					(2, 'sources', 'Sources', 'table_view', @sources),
					(3, 'entities', 'Entities', 'category', @entities),
					(4, 'relationships', 'Relationships', 'grid_on', @relationships),
					(5, 'calendars', 'Calendars', 'calendar_month', @calendars),
					(6, 'rules', 'Rules', 'rule', @rules),
					(7, 'runs', 'Runs', 'history', @runs)) f (sortOrder, id, label, icon, children)
				ORDER BY f.sortOrder
				FOR JSON PATH) [results]

		RETURN 1
	END TRY
	BEGIN CATCH
		SELECT (
			SELECT ERROR_MESSAGE() [errorMessage], ERROR_NUMBER() [errorCode]
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
		RETURN 0
	END CATCH
END
GO
