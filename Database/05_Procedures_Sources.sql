/****** Object:  StoredProcedures for sources ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  StoredProcedure [dbo].[GetSources] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The sources of a profile with their columns
-- EXEC GetSources '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetSources]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetSourceJson(s.id) AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY s.name) + ']'
				FROM Sources s (NOLOCK)
				WHERE s.profileId = @ProfileId), '[]') [results]

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

/****** Object:  StoredProcedure [dbo].[GetSource] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- One source of a profile
-- EXEC GetSource '<profile id>', '<source id>'
CREATE OR ALTER PROCEDURE [dbo].[GetSource]
	@ProfileId VARCHAR(50),
	@SourceId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Sources (NOLOCK) WHERE id = @SourceId AND profileId = @ProfileId)
			THROW 51000, 'Source not found', 1

		SELECT dbo.GetSourceJson(@SourceId) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveSource] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a source to a profile, or changes one when @SourceId is given
-- a table or view needs its schema and name, a query needs its select (the API checks it is only a select first)
-- EXEC SaveSource '<profile id>', NULL, '<connection id>', 'Jobs', 'table', 'src', 'Job', NULL
CREATE OR ALTER PROCEDURE [dbo].[SaveSource]
	@ProfileId VARCHAR(50),
	@SourceId VARCHAR(50) = NULL,
	@ConnectionId VARCHAR(50),
	@Name NVARCHAR(200),
	@Kind VARCHAR(10),
	@Schema NVARCHAR(128) = NULL,
	@ObjectName NVARCHAR(128) = NULL,
	@QueryText NVARCHAR(MAX) = NULL
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1
		IF NOT EXISTS (SELECT 1 FROM Connections (NOLOCK) WHERE id = @ConnectionId)
			THROW 51000, 'Connection not found', 1
		IF @Name = ''
			THROW 51000, 'A source needs a name', 1
		IF ISNULL(@Kind, '') NOT IN ('table', 'view', 'query')
			THROW 51000, 'A source is a table, a view or a query', 1
		IF @Kind = 'query' AND NULLIF(LTRIM(RTRIM(@QueryText)), '') IS NULL
			THROW 51000, 'A query source needs its select', 1
		IF @Kind <> 'query' AND (NULLIF(LTRIM(RTRIM(@Schema)), '') IS NULL OR NULLIF(LTRIM(RTRIM(@ObjectName)), '') IS NULL)
			THROW 51000, 'A table or view source needs its schema and name', 1
		IF EXISTS (SELECT 1 FROM Sources (NOLOCK) WHERE profileId = @ProfileId AND [name] = @Name AND id <> ISNULL(@SourceId, ''))
			THROW 51000, 'Another source in this profile already has this name', 1

		IF @Kind = 'query'
			SELECT @Schema = NULL, @ObjectName = NULL
		ELSE
			SET @QueryText = NULL

		IF @SourceId IS NULL
		BEGIN
			SET @SourceId = CAST(NEWID() AS VARCHAR(50))
			INSERT INTO Sources (id, profileId, connectionId, [name], kind, [schema], objectName, queryText)
				VALUES (@SourceId, @ProfileId, @ConnectionId, @Name, @Kind, @Schema, @ObjectName, @QueryText)
		END
		ELSE
		BEGIN
			UPDATE Sources
				SET connectionId = @ConnectionId, [name] = @Name, kind = @Kind, [schema] = @Schema, objectName = @ObjectName, queryText = @QueryText,
					modified = SYSUTCDATETIME()
				WHERE id = @SourceId AND profileId = @ProfileId
			IF @@ROWCOUNT = 0
				THROW 51000, 'Source not found', 1
		END

		SELECT dbo.GetSourceJson(@SourceId) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveSourceColumns] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Keeps the columns the API read from the source, [{ name, sqlType, nullable }]
-- EXEC SaveSourceColumns '<profile id>', '<source id>', '[{"name":"JobNo","sqlType":"nvarchar","nullable":false}]'
CREATE OR ALTER PROCEDURE [dbo].[SaveSourceColumns]
	@ProfileId VARCHAR(50),
	@SourceId VARCHAR(50),
	@Columns NVARCHAR(MAX)
AS
BEGIN
	BEGIN TRY
		IF ISNULL(ISJSON(@Columns), 0) = 0
			THROW 51000, 'The columns are not valid json', 1
		UPDATE Sources
			SET [columns] = @Columns, columnsRead = SYSUTCDATETIME()
			WHERE id = @SourceId AND profileId = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Source not found', 1

		SELECT dbo.GetSourceJson(@SourceId) [results]
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

/****** Object:  StoredProcedure [dbo].[DeleteSource] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes a source that no entity type or relationship reads from
-- EXEC DeleteSource '<profile id>', '<source id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteSource]
	@ProfileId VARCHAR(50),
	@SourceId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF EXISTS (SELECT 1 FROM EntityTypes (NOLOCK) WHERE sourceId = @SourceId)
			OR EXISTS (SELECT 1 FROM Relationships (NOLOCK) WHERE sourceId = @SourceId)
			THROW 51000, 'An entity type or relationship reads from this source, unmap it first', 1
		DELETE FROM Sources WHERE id = @SourceId AND profileId = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Source not found', 1

		SELECT '{}' [results]
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
