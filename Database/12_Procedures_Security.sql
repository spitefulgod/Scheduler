/****** Object:  StoredProcedures for domain user security and user settings ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  StoredProcedure [dbo].[GetUserRoles] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The roles given out, the site owners when @SiteOnly is 1, one profile's users when @ProfileId is given, otherwise all of them
-- the API reads all of them to work out what the signed in user can do, users can be domain groups too
-- EXEC GetUserRoles
-- EXEC GetUserRoles NULL, 1
-- EXEC GetUserRoles '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetUserRoles]
	@ProfileId VARCHAR(50) = NULL,
	@SiteOnly BIT = 0
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL((
			SELECT u.id, u.userName, u.profileId, p.name [profileName], u.role, u.created, u.createdBy
				FROM UserRoles u (NOLOCK)
					LEFT JOIN Profiles p (NOLOCK) ON p.id = u.profileId
				WHERE (@SiteOnly = 1 AND u.profileId IS NULL)
					OR (@SiteOnly = 0 AND @ProfileId IS NOT NULL AND u.profileId = @ProfileId)
					OR (@SiteOnly = 0 AND @ProfileId IS NULL)
				ORDER BY p.name, u.role, u.userName
				FOR JSON PATH, INCLUDE_NULL_VALUES), '[]') [results]

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

/****** Object:  StoredProcedure [dbo].[ClaimSiteOwner] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- A new site has no owner, the first user to sign in becomes it so somebody can give out the other roles
-- does nothing once there is a site owner, gives back every role like GetUserRoles
-- EXEC ClaimSiteOwner 'CONTOSO\jsmith'
CREATE OR ALTER PROCEDURE [dbo].[ClaimSiteOwner]
	@UserName NVARCHAR(256)
AS
BEGIN
	BEGIN TRY
		SET @UserName = LTRIM(RTRIM(ISNULL(@UserName, '')))
		IF @UserName = ''
			THROW 51000, 'A user name is needed', 1

		BEGIN TRANSACTION
		IF NOT EXISTS (SELECT 1 FROM UserRoles WITH (UPDLOCK, HOLDLOCK) WHERE role = 'SiteOwner')
			INSERT INTO UserRoles (userName, profileId, role, createdBy)
				VALUES (@UserName, NULL, 'SiteOwner', @UserName)
		COMMIT TRANSACTION

		EXEC GetUserRoles
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

/****** Object:  StoredProcedure [dbo].[SaveUserRole] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Gives a user or group a role, SiteOwner without a profile, the project roles with one
-- gives back the site owners or the profile's users, the same as GetUserRoles
-- EXEC SaveUserRole NULL, 'CONTOSO\jsmith', 'SiteOwner', 'CONTOSO\admin'
-- EXEC SaveUserRole '<profile id>', 'CONTOSO\Planners', 'ProjectSolver', 'CONTOSO\admin'
CREATE OR ALTER PROCEDURE [dbo].[SaveUserRole]
	@ProfileId VARCHAR(50) = NULL,
	@UserName NVARCHAR(256),
	@Role VARCHAR(20),
	@CreatedBy NVARCHAR(256) = NULL
AS
BEGIN
	BEGIN TRY
		SET @UserName = LTRIM(RTRIM(ISNULL(@UserName, '')))
		IF @UserName = ''
			THROW 51000, 'Enter the domain user or group, e.g. CONTOSO\jsmith', 1
		IF @Role NOT IN ('SiteOwner', 'ProjectOwner', 'ProjectReader', 'ProjectSolver')
			THROW 51000, 'The role must be SiteOwner, ProjectOwner, ProjectReader or ProjectSolver', 1
		IF @Role = 'SiteOwner' AND @ProfileId IS NOT NULL
			THROW 51000, 'Site owners are for the whole site, not one profile', 1
		IF @Role <> 'SiteOwner' AND @ProfileId IS NULL
			THROW 51000, 'Project roles are given on a profile', 1
		IF @ProfileId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1

		IF NOT EXISTS (SELECT 1 FROM UserRoles WHERE userName = @UserName AND role = @Role AND ISNULL(profileId, '') = ISNULL(@ProfileId, ''))
			INSERT INTO UserRoles (userName, profileId, role, createdBy)
				VALUES (@UserName, @ProfileId, @Role, @CreatedBy)

		IF @ProfileId IS NULL
			EXEC GetUserRoles NULL, 1
		ELSE
			EXEC GetUserRoles @ProfileId
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

/****** Object:  StoredProcedure [dbo].[DeleteUserRole] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Takes a role away, a site owner role when @ProfileId is NULL, the last site owner can't be taken away
-- EXEC DeleteUserRole NULL, '<user role id>'
-- EXEC DeleteUserRole '<profile id>', '<user role id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteUserRole]
	@ProfileId VARCHAR(50) = NULL,
	@UserRoleId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM UserRoles WHERE id = @UserRoleId AND ISNULL(profileId, '') = ISNULL(@ProfileId, ''))
			THROW 51000, 'Role not found', 1
		IF @ProfileId IS NULL AND (SELECT COUNT(*) FROM UserRoles WHERE role = 'SiteOwner') <= 1
			THROW 51000, 'The site needs at least one site owner', 1

		DELETE FROM UserRoles WHERE id = @UserRoleId

		IF @ProfileId IS NULL
			EXEC GetUserRoles NULL, 1
		ELSE
			EXEC GetUserRoles @ProfileId
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

/****** Object:  StoredProcedure [dbo].[GetUserSettings] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- A user's own screen settings, {} when they haven't saved any
-- EXEC GetUserSettings 'CONTOSO\jsmith'
CREATE OR ALTER PROCEDURE [dbo].[GetUserSettings]
	@UserName NVARCHAR(256)
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL((SELECT settings FROM UserSettings (NOLOCK) WHERE userName = @UserName), '{}') [results]
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

/****** Object:  StoredProcedure [dbo].[SaveUserSettings] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Keeps a user's screen settings, the values sent are merged into the ones already saved
-- EXEC SaveUserSettings 'CONTOSO\jsmith', '{"propertiesCollapsed":true}'
CREATE OR ALTER PROCEDURE [dbo].[SaveUserSettings]
	@UserName NVARCHAR(256),
	@Settings NVARCHAR(MAX)
AS
BEGIN
	BEGIN TRY
		IF ISJSON(@Settings) = 0 OR LEFT(LTRIM(@Settings), 1) <> '{'
			THROW 51000, 'Settings must be a json object', 1

		DECLARE @Saved NVARCHAR(MAX) = ISNULL((SELECT settings FROM UserSettings WHERE userName = @UserName), '{}')
		-- each value sent replaces the saved one, the rest are kept, strings are quoted and the rest are json as they are
		SELECT @Saved = '{' + ISNULL(STRING_AGG(CAST('"' + STRING_ESCAPE(v.[key], 'json') + '":'
				+ CASE v.[type] WHEN 1 THEN '"' + STRING_ESCAPE(v.[value], 'json') + '"' WHEN 0 THEN 'null' ELSE v.[value] END AS NVARCHAR(MAX)), ','), '') + '}'
			FROM (
				SELECT [key], [value], [type] FROM OPENJSON(@Settings)
				UNION ALL
				SELECT s.[key], s.[value], s.[type] FROM OPENJSON(@Saved) s
					WHERE NOT EXISTS (SELECT 1 FROM OPENJSON(@Settings) n WHERE n.[key] = s.[key])) v

		UPDATE UserSettings SET settings = @Saved, modified = SYSUTCDATETIME() WHERE userName = @UserName
		IF @@ROWCOUNT = 0
			INSERT INTO UserSettings (userName, settings) VALUES (@UserName, @Saved)

		SELECT @Saved [results]
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
