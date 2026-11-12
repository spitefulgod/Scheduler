/****** Object:  StoredProcedures for connections ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  StoredProcedure [dbo].[GetConnections] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Every connection without its password
-- EXEC GetConnections
CREATE OR ALTER PROCEDURE [dbo].[GetConnections]
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetConnectionJson(c.id) AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY c.name) + ']'
				FROM Connections c (NOLOCK)), '[]') [results]

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

/****** Object:  StoredProcedure [dbo].[GetConnection] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- One connection without its password
-- EXEC GetConnection '<connection id>'
CREATE OR ALTER PROCEDURE [dbo].[GetConnection]
	@ConnectionId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Connections (NOLOCK) WHERE id = @ConnectionId)
			THROW 51000, 'Connection not found', 1

		SELECT dbo.GetConnectionJson(@ConnectionId) [results]
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

/****** Object:  StoredProcedure [dbo].[GetConnectionDetails] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- What the API needs to connect, with the encrypted password, it stays in the API and never goes to the client
-- EXEC GetConnectionDetails '<connection id>'
CREATE OR ALTER PROCEDURE [dbo].[GetConnectionDetails]
	@ConnectionId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Connections (NOLOCK) WHERE id = @ConnectionId)
			THROW 51000, 'Connection not found', 1

		SELECT (
			SELECT c.id, c.typeId, JSON_QUERY(c.settings) [settings], c.secret
				FROM Connections c (NOLOCK)
				WHERE c.id = @ConnectionId
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveConnection] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a connection, or changes one when @ConnectionId is given
-- @Secret is the password already encrypted by the API, NULL keeps the one it has, @ClearSecret removes it (Windows sign in)
-- the demo connection can't be changed
-- EXEC SaveConnection NULL, 'ERP', 'SQLSERVER', '{"server":"PLEXSERVER","database":"Erp","user":"","trustServerCertificate":true}', NULL, 1
CREATE OR ALTER PROCEDURE [dbo].[SaveConnection]
	@ConnectionId VARCHAR(50) = NULL,
	@Name NVARCHAR(200),
	@TypeId VARCHAR(50),
	@Settings NVARCHAR(MAX),
	@Secret NVARCHAR(MAX) = NULL,
	@ClearSecret BIT = 0
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF @Name = ''
			THROW 51000, 'A connection needs a name', 1
		IF @TypeId = 'DEMO' OR NOT EXISTS (SELECT 1 FROM ConnectionTypes (NOLOCK) WHERE id = @TypeId)
			THROW 51000, 'That isn''t a kind of connection that can be used', 1
		IF ISNULL(ISJSON(@Settings), 0) = 0
			THROW 51000, 'The connection''s settings are not valid json', 1
		IF EXISTS (SELECT 1 FROM Connections (NOLOCK) WHERE [name] = @Name AND id <> ISNULL(@ConnectionId, ''))
			THROW 51000, 'Another connection already has this name', 1

		IF @ConnectionId IS NULL
		BEGIN
			SET @ConnectionId = CAST(NEWID() AS VARCHAR(50))
			INSERT INTO Connections (id, [name], typeId, settings, secret)
				VALUES (@ConnectionId, @Name, @TypeId, @Settings, CASE WHEN ISNULL(@ClearSecret, 0) = 1 THEN NULL ELSE @Secret END)
		END
		ELSE
		BEGIN
			IF EXISTS (SELECT 1 FROM Connections (NOLOCK) WHERE id = @ConnectionId AND typeId = 'DEMO')
				THROW 51000, 'The demo connection can''t be changed', 1
			-- a changed connection needs testing again
			UPDATE Connections
				SET [name] = @Name, typeId = @TypeId, settings = @Settings,
					secret = CASE WHEN ISNULL(@ClearSecret, 0) = 1 THEN NULL ELSE ISNULL(@Secret, secret) END,
					lastTested = NULL, lastTestOk = NULL, lastTestMessage = NULL, modified = SYSUTCDATETIME()
				WHERE id = @ConnectionId
			IF @@ROWCOUNT = 0
				THROW 51000, 'Connection not found', 1
		END

		SELECT dbo.GetConnectionJson(@ConnectionId) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveConnectionTest] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Records how the last test of a connection went
-- EXEC SaveConnectionTest '<connection id>', 1, 'Connected'
CREATE OR ALTER PROCEDURE [dbo].[SaveConnectionTest]
	@ConnectionId VARCHAR(50),
	@Ok BIT,
	@Message NVARCHAR(2000)
AS
BEGIN
	BEGIN TRY
		UPDATE Connections
			SET lastTested = SYSUTCDATETIME(), lastTestOk = @Ok, lastTestMessage = LEFT(@Message, 2000)
			WHERE id = @ConnectionId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Connection not found', 1

		SELECT dbo.GetConnectionJson(@ConnectionId) [results]
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

/****** Object:  StoredProcedure [dbo].[DeleteConnection] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes a connection that no source uses
-- EXEC DeleteConnection '<connection id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteConnection]
	@ConnectionId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF EXISTS (SELECT 1 FROM Connections (NOLOCK) WHERE id = @ConnectionId AND typeId = 'DEMO')
			THROW 51000, 'The demo connection can''t be removed', 1
		IF EXISTS (SELECT 1 FROM Sources (NOLOCK) WHERE connectionId = @ConnectionId)
			THROW 51000, 'Sources use this connection, remove them first', 1
		DELETE FROM Connections WHERE id = @ConnectionId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Connection not found', 1

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
