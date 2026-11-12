/****** Object:  StoredProcedures for the rule catalogue, value scales and status ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  StoredProcedure [dbo].[GetRuleTypes] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The rule block catalogue
-- EXEC GetRuleTypes
CREATE OR ALTER PROCEDURE [dbo].[GetRuleTypes]
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL((
			SELECT t.id, t.name, t.category, t.description, t.compilesTo, t.supportsHard, t.supportsSoft, JSON_QUERY(t.parameters) [parameters]
				FROM RuleTypes t (NOLOCK)
				ORDER BY t.sortOrder
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

/****** Object:  StoredProcedure [dbo].[GetValueScales] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The built in value scales and a profile's own, with their values
-- EXEC GetValueScales '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetValueScales]
	@ProfileId VARCHAR(50) = NULL
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL((
			SELECT s.id, s.profileId, s.name,
				JSON_QUERY(ISNULL((SELECT v.code, v.label, v.meaning, v.weight, v.colour
					FROM ValueScaleValues v (NOLOCK)
					WHERE v.valueScaleId = s.id
					ORDER BY v.sortOrder
					FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [values]
				FROM ValueScales s (NOLOCK)
				WHERE s.profileId IS NULL OR s.profileId = @ProfileId
				ORDER BY s.name
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

/****** Object:  StoredProcedure [dbo].[GetStatus] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Which server and database the API is using and the last update script run, for the status bar
-- EXEC GetStatus
CREATE OR ALTER PROCEDURE [dbo].[GetStatus]
AS
BEGIN
	BEGIN TRY
		SELECT (
			SELECT @@SERVERNAME [server], DB_NAME() [database],
				(SELECT TOP 1 [version] FROM SchemaVersions (NOLOCK) ORDER BY [version] DESC) [schemaVersion]
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
