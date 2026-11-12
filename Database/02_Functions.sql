/****** Object:  Functions for SchedulerMeta ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  UserDefinedFunction [dbo].[GetProfileJson] ******/
-- One profile as json
-- SELECT dbo.GetProfileJson('<profile id>')
CREATE OR ALTER FUNCTION [dbo].[GetProfileJson](@ProfileId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT p.id, p.name, p.description, p.timeUnitMinutes, JSON_QUERY(p.settings) [settings], p.created, p.modified
			FROM Profiles p (NOLOCK)
			WHERE p.id = @ProfileId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO

/****** Object:  UserDefinedFunction [dbo].[GetConnectionJson] ******/
-- One connection as json, never with its password, only whether it has one
-- SELECT dbo.GetConnectionJson('<connection id>')
CREATE OR ALTER FUNCTION [dbo].[GetConnectionJson](@ConnectionId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT c.id, c.name, c.typeId, t.name [typeName], JSON_QUERY(c.settings) [settings],
			CAST(CASE WHEN c.secret IS NULL THEN 0 ELSE 1 END AS BIT) [hasPassword],
			c.lastTested, c.lastTestOk, c.lastTestMessage, c.created, c.modified
			FROM Connections c (NOLOCK)
				JOIN ConnectionTypes t (NOLOCK) ON t.id = c.typeId
			WHERE c.id = @ConnectionId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO

/****** Object:  UserDefinedFunction [dbo].[GetSourceJson] ******/
-- One source as json with its columns
-- SELECT dbo.GetSourceJson('<source id>')
CREATE OR ALTER FUNCTION [dbo].[GetSourceJson](@SourceId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT s.id, s.profileId, s.connectionId, c.name [connectionName], s.name, s.kind, s.[schema], s.objectName, s.queryText,
			JSON_QUERY(s.[columns]) [columns], s.columnsRead, s.created, s.modified
			FROM Sources s (NOLOCK)
				JOIN Connections c (NOLOCK) ON c.id = s.connectionId
			WHERE s.id = @SourceId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO

/****** Object:  UserDefinedFunction [dbo].[GetEntityJson] ******/
-- One entity type as json with its attributes
-- SELECT dbo.GetEntityJson('<entity type id>')
CREATE OR ALTER FUNCTION [dbo].[GetEntityJson](@EntityTypeId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT e.id, e.profileId, e.name, e.kind, e.[description], e.sourceId, s.name [sourceName], e.durationFormula,
			JSON_QUERY(ISNULL((SELECT a.id, a.name, a.dataType, a.[role], a.sourceColumn, a.referencesEntityTypeId, r.name [referencesEntity], a.sortOrder
				FROM Attributes a (NOLOCK)
					LEFT JOIN EntityTypes r (NOLOCK) ON r.id = a.referencesEntityTypeId
				WHERE a.entityTypeId = e.id
				ORDER BY a.sortOrder, a.name
				FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [attributes],
			e.created, e.modified
			FROM EntityTypes e (NOLOCK)
				LEFT JOIN Sources s (NOLOCK) ON s.id = e.sourceId
			WHERE e.id = @EntityTypeId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO

/****** Object:  UserDefinedFunction [dbo].[GetRelationshipJson] ******/
-- One relationship as json, without its values
-- SELECT dbo.GetRelationshipJson('<relationship id>')
CREATE OR ALTER FUNCTION [dbo].[GetRelationshipJson](@RelationshipId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT r.id, r.profileId, r.name, r.entityTypeAId, a.name [entityA], r.entityTypeBId, b.name [entityB], r.valueScaleId, vs.name [valueScaleName],
			r.defaultValue, r.[symmetric], r.editor, r.drives, r.sourceId, s.name [sourceName], r.sourceAColumn, r.sourceBColumn, r.sourceValueColumn,
			(SELECT COUNT(*) FROM RelationshipValues v (NOLOCK) WHERE v.relationshipId = r.id) [valueCount], r.created, r.modified
			FROM Relationships r (NOLOCK)
				JOIN EntityTypes a (NOLOCK) ON a.id = r.entityTypeAId
				JOIN EntityTypes b (NOLOCK) ON b.id = r.entityTypeBId
				JOIN ValueScales vs (NOLOCK) ON vs.id = r.valueScaleId
				LEFT JOIN Sources s (NOLOCK) ON s.id = r.sourceId
			WHERE r.id = @RelationshipId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO

/****** Object:  UserDefinedFunction [dbo].[GetCalendarJson] ******/
-- One calendar as json with its weekly shifts and exceptions
-- SELECT dbo.GetCalendarJson('<calendar id>')
CREATE OR ALTER FUNCTION [dbo].[GetCalendarJson](@CalendarId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT c.id, c.profileId, c.name, c.parentCalendarId, p.name [parentName], c.resourceEntityTypeId, e.name [resourceEntity], c.resourceKey,
			JSON_QUERY(ISNULL((SELECT x.[dayOfWeek], CONVERT(VARCHAR(5), x.startTime, 108) [startTime], CONVERT(VARCHAR(5), x.endTime, 108) [endTime]
				FROM CalendarShifts x (NOLOCK)
				WHERE x.calendarId = c.id
				ORDER BY x.[dayOfWeek], x.startTime
				FOR JSON PATH), '[]')) [shifts],
			JSON_QUERY(ISNULL((SELECT x.id, x.startTime, x.endTime, x.kind, x.reason
				FROM CalendarExceptions x (NOLOCK)
				WHERE x.calendarId = c.id
				ORDER BY x.startTime
				FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [exceptions]
			FROM Calendars c (NOLOCK)
				LEFT JOIN Calendars p (NOLOCK) ON p.id = c.parentCalendarId
				LEFT JOIN EntityTypes e (NOLOCK) ON e.id = c.resourceEntityTypeId
			WHERE c.id = @CalendarId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO

/****** Object:  UserDefinedFunction [dbo].[GetRuleJson] ******/
-- One rule as json, blocks is the rule builder's json
-- SELECT dbo.GetRuleJson('<rule id>')
CREATE OR ALTER FUNCTION [dbo].[GetRuleJson](@RuleId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT r.id, r.profileId, r.name, r.ruleTypeId, t.name [ruleTypeName], t.category, r.enabled, r.strength, r.[weight],
			JSON_QUERY(r.blocks) [blocks], r.sortOrder, r.created, r.modified
			FROM Rules r (NOLOCK)
				JOIN RuleTypes t (NOLOCK) ON t.id = r.ruleTypeId
			WHERE r.id = @RuleId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO

/****** Object:  UserDefinedFunction [dbo].[GetSolveRunJson] ******/
-- One solve run as json without its assignments
-- SELECT dbo.GetSolveRunJson('<run id>')
CREATE OR ALTER FUNCTION [dbo].[GetSolveRunJson](@RunId VARCHAR(50))
RETURNS NVARCHAR(MAX)
AS
BEGIN
	RETURN (
		SELECT r.id, r.profileId, r.kind, r.parentRunId, r.[status], r.requested, r.[started], r.finished, r.timeLimitSeconds, r.horizonStart, r.horizonEnd,
			JSON_QUERY(r.settings) [settings], r.objective, r.[message], JSON_QUERY(r.infeasibility) [infeasibility], r.published, r.publishedAt, r.discarded,
			JSON_QUERY(r.details) [details], JSON_QUERY(r.diagnostics) [diagnostics], JSON_QUERY(r.unscheduled) [unscheduled],
			JSON_QUERY(r.changes) [changes], JSON_QUERY(r.stats) [stats],
			(SELECT COUNT(*) FROM ScheduleAssignments a (NOLOCK) WHERE a.solveRunId = r.id) [assignmentCount]
			FROM SolveRuns r (NOLOCK)
			WHERE r.id = @RunId
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
END
GO
