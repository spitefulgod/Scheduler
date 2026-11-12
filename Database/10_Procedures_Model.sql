/****** Object:  StoredProcedures for entities, relationships, value scales, calendars and rules ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/****** Object:  StoredProcedure [dbo].[GetEntities] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- A profile's entity types with their attributes, items first then resources then references
-- EXEC GetEntities '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetEntities]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetEntityJson(e.id) AS NVARCHAR(MAX)), ',')
				WITHIN GROUP (ORDER BY CASE e.kind WHEN 'Item' THEN 1 WHEN 'Resource' THEN 2 ELSE 3 END, e.name) + ']'
				FROM EntityTypes e (NOLOCK)
				WHERE e.profileId = @ProfileId), '[]') [results]
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

/****** Object:  StoredProcedure [dbo].[GetEntity] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- One entity type with its attributes
-- EXEC GetEntity '<profile id>', '<entity type id>'
CREATE OR ALTER PROCEDURE [dbo].[GetEntity]
	@ProfileId VARCHAR(50),
	@EntityTypeId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM EntityTypes (NOLOCK) WHERE id = @EntityTypeId AND profileId = @ProfileId)
			THROW 51000, 'Entity not found', 1

		SELECT dbo.GetEntityJson(@EntityTypeId) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveEntity] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds an entity type, or changes one when @EntityTypeId is given
-- kind is Item (gets scheduled), Resource or Reference, the source is where its rows are read from
-- EXEC SaveEntity '<profile id>', NULL, 'Job', 'Item', 'Jobs to mould', '<source id>', 'qty * cycle_secs / 60'
CREATE OR ALTER PROCEDURE [dbo].[SaveEntity]
	@ProfileId VARCHAR(50),
	@EntityTypeId VARCHAR(50) = NULL,
	@Name NVARCHAR(100),
	@Kind VARCHAR(20),
	@Description NVARCHAR(2000) = NULL,
	@SourceId VARCHAR(50) = NULL,
	@DurationFormula NVARCHAR(1000) = NULL
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF @Name = ''
			THROW 51000, 'An entity needs a name', 1
		IF @Kind NOT IN ('Item', 'Resource', 'Reference')
			THROW 51000, 'The kind needs to be Item, Resource or Reference', 1
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1
		IF NULLIF(@SourceId, '') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Sources (NOLOCK) WHERE id = @SourceId AND profileId = @ProfileId)
			THROW 51000, 'The source is not in this profile', 1
		IF EXISTS (SELECT 1 FROM EntityTypes (NOLOCK) WHERE profileId = @ProfileId AND [name] = @Name AND id <> ISNULL(@EntityTypeId, ''))
			THROW 51000, 'Another entity already has this name', 1

		IF @EntityTypeId IS NULL
		BEGIN
			SET @EntityTypeId = CAST(NEWID() AS VARCHAR(50))
			INSERT INTO EntityTypes (id, profileId, [name], kind, [description], sourceId, durationFormula)
				VALUES (@EntityTypeId, @ProfileId, @Name, @Kind, NULLIF(LTRIM(RTRIM(@Description)), ''), NULLIF(@SourceId, ''), NULLIF(LTRIM(RTRIM(@DurationFormula)), ''))
		END
		ELSE
		BEGIN
			UPDATE EntityTypes
				SET [name] = @Name, kind = @Kind, [description] = NULLIF(LTRIM(RTRIM(@Description)), ''), sourceId = NULLIF(@SourceId, ''),
					durationFormula = NULLIF(LTRIM(RTRIM(@DurationFormula)), ''), modified = SYSUTCDATETIME()
				WHERE id = @EntityTypeId AND profileId = @ProfileId
			IF @@ROWCOUNT = 0
				THROW 51000, 'Entity not found', 1
		END

		SELECT dbo.GetEntityJson(@EntityTypeId) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveAttributes] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Sets an entity type's attributes to the list given, ones left out are removed
-- @Attributes is json [{ id, name, dataType, role, sourceColumn, referencesEntityTypeId, sortOrder }], no id adds one
-- EXEC SaveAttributes '<profile id>', '<entity type id>', '[{"name":"qty","dataType":"INTEGER","role":"Quantity","sourceColumn":"Quantity"}]'
CREATE OR ALTER PROCEDURE [dbo].[SaveAttributes]
	@ProfileId VARCHAR(50),
	@EntityTypeId VARCHAR(50),
	@Attributes NVARCHAR(MAX)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM EntityTypes (NOLOCK) WHERE id = @EntityTypeId AND profileId = @ProfileId)
			THROW 51000, 'Entity not found', 1
		IF ISJSON(@Attributes) = 0
			THROW 51000, 'The attributes need to be a json list', 1

		DECLARE @list TABLE (id VARCHAR(50), [name] NVARCHAR(100), dataType VARCHAR(20), [role] VARCHAR(20), sourceColumn NVARCHAR(128),
			referencesEntityTypeId VARCHAR(50), sortOrder INT)
		INSERT INTO @list (id, [name], dataType, [role], sourceColumn, referencesEntityTypeId, sortOrder)
			SELECT ISNULL(NULLIF(a.id, ''), CAST(NEWID() AS VARCHAR(50))), LTRIM(RTRIM(a.[name])), ISNULL(NULLIF(a.dataType, ''), 'STRING'),
				ISNULL(NULLIF(a.[role], ''), 'Custom'), NULLIF(LTRIM(RTRIM(a.sourceColumn)), ''), NULLIF(a.referencesEntityTypeId, ''),
				ISNULL(a.sortOrder, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
				FROM OPENJSON(@Attributes) WITH (id VARCHAR(50), [name] NVARCHAR(100), dataType VARCHAR(20), [role] VARCHAR(20), sourceColumn NVARCHAR(128),
					referencesEntityTypeId VARCHAR(50), sortOrder INT) a

		IF EXISTS (SELECT 1 FROM @list WHERE ISNULL([name], '') = '')
			THROW 51000, 'Every attribute needs a name', 1
		IF EXISTS (SELECT [name] FROM @list GROUP BY [name] HAVING COUNT(*) > 1)
			THROW 51000, 'Two attributes have the same name', 1
		IF EXISTS (SELECT [role] FROM @list WHERE [role] NOT IN ('Custom', 'Category', 'Size') GROUP BY [role] HAVING COUNT(*) > 1)
			THROW 51000, 'Only one attribute can have each role, apart from Custom, Category and Size', 1
		IF EXISTS (SELECT 1 FROM @list l WHERE l.referencesEntityTypeId IS NOT NULL
				AND NOT EXISTS (SELECT 1 FROM EntityTypes e (NOLOCK) WHERE e.id = l.referencesEntityTypeId AND e.profileId = @ProfileId))
			THROW 51000, 'An attribute references an entity that is not in this profile', 1

		BEGIN TRANSACTION
			DELETE a
				FROM Attributes a
				WHERE a.entityTypeId = @EntityTypeId AND NOT EXISTS (SELECT 1 FROM @list l WHERE l.id = a.id)
			MERGE Attributes t
				USING @list s ON t.id = s.id
				WHEN MATCHED THEN UPDATE SET [name] = s.[name], dataType = s.dataType, [role] = s.[role], sourceColumn = s.sourceColumn,
					referencesEntityTypeId = s.referencesEntityTypeId, sortOrder = s.sortOrder
				WHEN NOT MATCHED THEN INSERT (id, entityTypeId, [name], dataType, [role], sourceColumn, referencesEntityTypeId, sortOrder)
					VALUES (s.id, @EntityTypeId, s.[name], s.dataType, s.[role], s.sourceColumn, s.referencesEntityTypeId, s.sortOrder);
			UPDATE EntityTypes SET modified = SYSUTCDATETIME() WHERE id = @EntityTypeId
		COMMIT TRANSACTION

		SELECT dbo.GetEntityJson(@EntityTypeId) [results]
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

/****** Object:  StoredProcedure [dbo].[DeleteEntity] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes an entity type and its attributes, not while a relationship or calendar uses it
-- EXEC DeleteEntity '<profile id>', '<entity type id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteEntity]
	@ProfileId VARCHAR(50),
	@EntityTypeId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM EntityTypes (NOLOCK) WHERE id = @EntityTypeId AND profileId = @ProfileId)
			THROW 51000, 'Entity not found', 1
		IF EXISTS (SELECT 1 FROM Relationships (NOLOCK) WHERE entityTypeAId = @EntityTypeId OR entityTypeBId = @EntityTypeId)
			THROW 51000, 'A relationship uses this entity, remove it first', 1
		IF EXISTS (SELECT 1 FROM Calendars (NOLOCK) WHERE resourceEntityTypeId = @EntityTypeId)
			THROW 51000, 'A calendar uses this entity, remove it first', 1

		BEGIN TRANSACTION
			UPDATE Attributes SET referencesEntityTypeId = NULL WHERE referencesEntityTypeId = @EntityTypeId
			DELETE FROM EntityTypes WHERE id = @EntityTypeId
		COMMIT TRANSACTION

		SELECT (SELECT @EntityTypeId [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
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

/****** Object:  StoredProcedure [dbo].[GetRelationships] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- A profile's relationships, without their values
-- EXEC GetRelationships '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetRelationships]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetRelationshipJson(r.id) AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY r.name) + ']'
				FROM Relationships r (NOLOCK)
				WHERE r.profileId = @ProfileId), '[]') [results]
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

/****** Object:  StoredProcedure [dbo].[GetRelationship] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- One relationship with the values typed in or overridden, values read live from its source are added by the API
-- EXEC GetRelationship '<profile id>', '<relationship id>'
CREATE OR ALTER PROCEDURE [dbo].[GetRelationship]
	@ProfileId VARCHAR(50),
	@RelationshipId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Relationships (NOLOCK) WHERE id = @RelationshipId AND profileId = @ProfileId)
			THROW 51000, 'Relationship not found', 1

		SELECT JSON_MODIFY(dbo.GetRelationshipJson(@RelationshipId), '$.values', JSON_QUERY(ISNULL(
			(SELECT v.aKey, v.bKey, v.[value]
				FROM RelationshipValues v (NOLOCK)
				WHERE v.relationshipId = @RelationshipId
				ORDER BY v.aKey, v.bKey
				FOR JSON PATH), '[]'))) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveRelationship] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a relationship, or changes one when @RelationshipId is given
-- A x B with a value from the scale for each pair, the values can be typed in, read from a source, or both (typed in wins)
-- EXEC SaveRelationship '<profile id>', NULL, 'Compound on press', '<compound id>', '<press id>', 'GRADED', 'A', 0, 'Matrix', 'Allowed', NULL, NULL, NULL, NULL
CREATE OR ALTER PROCEDURE [dbo].[SaveRelationship]
	@ProfileId VARCHAR(50),
	@RelationshipId VARCHAR(50) = NULL,
	@Name NVARCHAR(200),
	@EntityTypeAId VARCHAR(50),
	@EntityTypeBId VARCHAR(50),
	@ValueScaleId VARCHAR(50),
	@DefaultValue NVARCHAR(50) = NULL,
	@Symmetric BIT = 0,
	@Editor VARCHAR(10) = 'Matrix',
	@Drives VARCHAR(50) = NULL,
	@SourceId VARCHAR(50) = NULL,
	@SourceAColumn NVARCHAR(128) = NULL,
	@SourceBColumn NVARCHAR(128) = NULL,
	@SourceValueColumn NVARCHAR(128) = NULL
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF @Name = ''
			THROW 51000, 'A relationship needs a name', 1
		IF (SELECT COUNT(*) FROM EntityTypes (NOLOCK) WHERE profileId = @ProfileId AND id IN (@EntityTypeAId, @EntityTypeBId)) <> CASE WHEN @EntityTypeAId = @EntityTypeBId THEN 1 ELSE 2 END
			THROW 51000, 'Both entities need to be in this profile', 1
		IF NOT EXISTS (SELECT 1 FROM ValueScales (NOLOCK) WHERE id = @ValueScaleId AND (profileId IS NULL OR profileId = @ProfileId))
			THROW 51000, 'Value scale not found', 1
		IF NULLIF(@DefaultValue, '') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ValueScaleValues (NOLOCK) WHERE valueScaleId = @ValueScaleId AND code = @DefaultValue)
				AND NOT EXISTS (SELECT 1 FROM ValueScaleValues (NOLOCK) WHERE valueScaleId = @ValueScaleId AND meaning = 'Numeric')
			THROW 51000, 'The default needs to be one of the scale''s values', 1
		IF @Symmetric = 1 AND @EntityTypeAId <> @EntityTypeBId
			THROW 51000, 'Only a relationship of an entity with itself can be symmetric', 1
		IF ISNULL(@Editor, '') NOT IN ('Matrix', 'List')
			THROW 51000, 'The editor needs to be Matrix or List', 1
		IF NULLIF(@SourceId, '') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Sources (NOLOCK) WHERE id = @SourceId AND profileId = @ProfileId)
			THROW 51000, 'The source is not in this profile', 1
		IF NULLIF(@SourceId, '') IS NOT NULL AND (NULLIF(@SourceAColumn, '') IS NULL OR NULLIF(@SourceBColumn, '') IS NULL)
			THROW 51000, 'Reading values from a source needs its A and B columns', 1
		IF EXISTS (SELECT 1 FROM Relationships (NOLOCK) WHERE profileId = @ProfileId AND [name] = @Name AND id <> ISNULL(@RelationshipId, ''))
			THROW 51000, 'Another relationship already has this name', 1

		IF @RelationshipId IS NULL
		BEGIN
			SET @RelationshipId = CAST(NEWID() AS VARCHAR(50))
			INSERT INTO Relationships (id, profileId, [name], entityTypeAId, entityTypeBId, valueScaleId, defaultValue, [symmetric], editor, drives,
					sourceId, sourceAColumn, sourceBColumn, sourceValueColumn)
				VALUES (@RelationshipId, @ProfileId, @Name, @EntityTypeAId, @EntityTypeBId, @ValueScaleId, NULLIF(@DefaultValue, ''), ISNULL(@Symmetric, 0),
					@Editor, NULLIF(@Drives, ''), NULLIF(@SourceId, ''), NULLIF(@SourceAColumn, ''), NULLIF(@SourceBColumn, ''), NULLIF(@SourceValueColumn, ''))
		END
		ELSE
		BEGIN
			UPDATE Relationships
				SET [name] = @Name, entityTypeAId = @EntityTypeAId, entityTypeBId = @EntityTypeBId, valueScaleId = @ValueScaleId, defaultValue = NULLIF(@DefaultValue, ''),
					[symmetric] = ISNULL(@Symmetric, 0), editor = @Editor, drives = NULLIF(@Drives, ''), sourceId = NULLIF(@SourceId, ''),
					sourceAColumn = NULLIF(@SourceAColumn, ''), sourceBColumn = NULLIF(@SourceBColumn, ''), sourceValueColumn = NULLIF(@SourceValueColumn, ''),
					modified = SYSUTCDATETIME()
				WHERE id = @RelationshipId AND profileId = @ProfileId
			IF @@ROWCOUNT = 0
				THROW 51000, 'Relationship not found', 1
		END

		SELECT dbo.GetRelationshipJson(@RelationshipId) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveRelationshipValues] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Sets values in a relationship, @Values is json [{ aKey, bKey, value }], a blank value clears the pair back to its default
-- @Replace = 1 clears every value first, e.g. for an import
-- EXEC SaveRelationshipValues '<profile id>', '<relationship id>', '[{"aKey":"NBR-70","bKey":"P01","value":"P"}]', 0
CREATE OR ALTER PROCEDURE [dbo].[SaveRelationshipValues]
	@ProfileId VARCHAR(50),
	@RelationshipId VARCHAR(50),
	@Values NVARCHAR(MAX),
	@Replace BIT = 0
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Relationships (NOLOCK) WHERE id = @RelationshipId AND profileId = @ProfileId)
			THROW 51000, 'Relationship not found', 1
		IF ISJSON(@Values) = 0
			THROW 51000, 'The values need to be a json list', 1

		DECLARE @list TABLE (aKey NVARCHAR(100), bKey NVARCHAR(100), [value] NVARCHAR(50))
		INSERT INTO @list (aKey, bKey, [value])
			SELECT LTRIM(RTRIM(v.aKey)), LTRIM(RTRIM(v.bKey)), MAX(NULLIF(LTRIM(RTRIM(v.[value])), ''))
				FROM OPENJSON(@Values) WITH (aKey NVARCHAR(100), bKey NVARCHAR(100), [value] NVARCHAR(50)) v
				WHERE ISNULL(v.aKey, '') <> '' AND ISNULL(v.bKey, '') <> ''
				GROUP BY LTRIM(RTRIM(v.aKey)), LTRIM(RTRIM(v.bKey))

		-- a value has to be on the scale, unless the scale holds numbers
		DECLARE @scaleId VARCHAR(50) = (SELECT valueScaleId FROM Relationships (NOLOCK) WHERE id = @RelationshipId)
		IF NOT EXISTS (SELECT 1 FROM ValueScaleValues (NOLOCK) WHERE valueScaleId = @scaleId AND meaning = 'Numeric')
			AND EXISTS (SELECT 1 FROM @list l WHERE l.[value] IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ValueScaleValues v (NOLOCK) WHERE v.valueScaleId = @scaleId AND v.code = l.[value]))
			THROW 51000, 'A value is not on the relationship''s scale', 1
		IF EXISTS (SELECT 1 FROM ValueScaleValues (NOLOCK) WHERE valueScaleId = @scaleId AND meaning = 'Numeric')
			AND EXISTS (SELECT 1 FROM @list WHERE [value] IS NOT NULL AND TRY_CAST([value] AS DECIMAL(18, 4)) IS NULL)
			THROW 51000, 'The values on this scale need to be numbers', 1

		BEGIN TRANSACTION
			IF @Replace = 1
				DELETE FROM RelationshipValues WHERE relationshipId = @RelationshipId
			DELETE v
				FROM RelationshipValues v
					JOIN @list l ON l.aKey = v.aKey AND l.bKey = v.bKey
				WHERE v.relationshipId = @RelationshipId AND l.[value] IS NULL
			MERGE RelationshipValues t
				USING (SELECT * FROM @list WHERE [value] IS NOT NULL) s ON t.relationshipId = @RelationshipId AND t.aKey = s.aKey AND t.bKey = s.bKey
				WHEN MATCHED THEN UPDATE SET [value] = s.[value]
				WHEN NOT MATCHED THEN INSERT (relationshipId, aKey, bKey, [value]) VALUES (@RelationshipId, s.aKey, s.bKey, s.[value]);
			UPDATE Relationships SET modified = SYSUTCDATETIME() WHERE id = @RelationshipId
		COMMIT TRANSACTION

		SELECT dbo.GetRelationshipJson(@RelationshipId) [results]
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

/****** Object:  StoredProcedure [dbo].[DeleteRelationship] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes a relationship and its values
-- EXEC DeleteRelationship '<profile id>', '<relationship id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteRelationship]
	@ProfileId VARCHAR(50),
	@RelationshipId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		DELETE FROM Relationships WHERE id = @RelationshipId AND profileId = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Relationship not found', 1

		SELECT (SELECT @RelationshipId [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveValueScale] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a profile's own value scale, or changes one, the built in scales can't be changed
-- @Values is json [{ code, label, meaning, weight, colour }], meaning is Forbidden, Allowed, Penalty or Numeric
-- EXEC SaveValueScale '<profile id>', NULL, 'Mix states', '[{"code":"Y","label":"Mix","meaning":"Allowed"},{"code":"N","label":"Never","meaning":"Forbidden"}]'
CREATE OR ALTER PROCEDURE [dbo].[SaveValueScale]
	@ProfileId VARCHAR(50),
	@ValueScaleId VARCHAR(50) = NULL,
	@Name NVARCHAR(100),
	@Values NVARCHAR(MAX)
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF @Name = ''
			THROW 51000, 'A scale needs a name', 1
		IF ISJSON(@Values) = 0
			THROW 51000, 'The values need to be a json list', 1
		IF @ValueScaleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ValueScales (NOLOCK) WHERE id = @ValueScaleId AND profileId = @ProfileId)
			THROW 51000, 'Only a profile''s own scales can be changed', 1

		DECLARE @list TABLE (code NVARCHAR(50), label NVARCHAR(100), meaning VARCHAR(20), [weight] INT, colour VARCHAR(20), sortOrder INT)
		INSERT INTO @list (code, label, meaning, [weight], colour, sortOrder)
			SELECT LTRIM(RTRIM(v.code)), ISNULL(NULLIF(LTRIM(RTRIM(v.label)), ''), LTRIM(RTRIM(v.code))), ISNULL(v.meaning, 'Allowed'), ISNULL(v.[weight], 0),
				NULLIF(v.colour, ''), ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
				FROM OPENJSON(@Values) WITH (code NVARCHAR(50), label NVARCHAR(100), meaning VARCHAR(20), [weight] INT, colour VARCHAR(20)) v
		IF NOT EXISTS (SELECT 1 FROM @list)
			THROW 51000, 'A scale needs at least one value', 1
		IF EXISTS (SELECT 1 FROM @list WHERE ISNULL(code, '') = '' OR meaning NOT IN ('Forbidden', 'Allowed', 'Penalty', 'Numeric'))
			THROW 51000, 'Every value needs a code and a meaning of Forbidden, Allowed, Penalty or Numeric', 1
		IF EXISTS (SELECT code FROM @list GROUP BY code HAVING COUNT(*) > 1)
			THROW 51000, 'Two values have the same code', 1

		BEGIN TRANSACTION
			IF @ValueScaleId IS NULL
			BEGIN
				SET @ValueScaleId = CAST(NEWID() AS VARCHAR(50))
				INSERT INTO ValueScales (id, profileId, [name]) VALUES (@ValueScaleId, @ProfileId, @Name)
			END
			ELSE
				UPDATE ValueScales SET [name] = @Name WHERE id = @ValueScaleId
			DELETE v FROM ValueScaleValues v WHERE v.valueScaleId = @ValueScaleId AND NOT EXISTS (SELECT 1 FROM @list l WHERE l.code = v.code)
			MERGE ValueScaleValues t
				USING @list s ON t.valueScaleId = @ValueScaleId AND t.code = s.code
				WHEN MATCHED THEN UPDATE SET label = s.label, meaning = s.meaning, [weight] = s.[weight], colour = s.colour, sortOrder = s.sortOrder
				WHEN NOT MATCHED THEN INSERT (valueScaleId, code, label, meaning, [weight], colour, sortOrder)
					VALUES (@ValueScaleId, s.code, s.label, s.meaning, s.[weight], s.colour, s.sortOrder);
		COMMIT TRANSACTION

		SELECT (SELECT s.id, s.profileId, s.name,
				JSON_QUERY(ISNULL((SELECT v.code, v.label, v.meaning, v.weight, v.colour
					FROM ValueScaleValues v (NOLOCK)
					WHERE v.valueScaleId = s.id
					ORDER BY v.sortOrder
					FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [values]
				FROM ValueScales s (NOLOCK)
				WHERE s.id = @ValueScaleId
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) [results]
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

/****** Object:  StoredProcedure [dbo].[DeleteValueScale] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes a profile's own value scale, not while a relationship uses it
-- EXEC DeleteValueScale '<profile id>', '<value scale id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteValueScale]
	@ProfileId VARCHAR(50),
	@ValueScaleId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF EXISTS (SELECT 1 FROM Relationships (NOLOCK) WHERE valueScaleId = @ValueScaleId)
			THROW 51000, 'A relationship uses this scale', 1
		DELETE FROM ValueScales WHERE id = @ValueScaleId AND profileId = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Only a profile''s own scales can be removed', 1

		SELECT (SELECT @ValueScaleId [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
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

/****** Object:  StoredProcedure [dbo].[GetCalendars] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- A profile's calendars with their shifts and exceptions, the ones with no parent first
-- EXEC GetCalendars '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetCalendars]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetCalendarJson(c.id) AS NVARCHAR(MAX)), ',')
				WITHIN GROUP (ORDER BY CASE WHEN c.parentCalendarId IS NULL THEN 0 ELSE 1 END, c.name) + ']'
				FROM Calendars c (NOLOCK)
				WHERE c.profileId = @ProfileId), '[]') [results]
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

/****** Object:  StoredProcedure [dbo].[SaveCalendar] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a calendar, or changes one, with its weekly shifts and exceptions
-- @Shifts is json [{ dayOfWeek, startTime, endTime }], @Exceptions is json [{ startTime, endTime, kind, reason }], both replace what's there
-- EXEC SaveCalendar '<profile id>', NULL, 'Site', NULL, NULL, NULL, '[{"dayOfWeek":1,"startTime":"06:00","endTime":"22:00"}]', '[]'
CREATE OR ALTER PROCEDURE [dbo].[SaveCalendar]
	@ProfileId VARCHAR(50),
	@CalendarId VARCHAR(50) = NULL,
	@Name NVARCHAR(200),
	@ParentCalendarId VARCHAR(50) = NULL,
	@ResourceEntityTypeId VARCHAR(50) = NULL,
	@ResourceKey NVARCHAR(100) = NULL,
	@Shifts NVARCHAR(MAX) = '[]',
	@Exceptions NVARCHAR(MAX) = '[]'
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF @Name = ''
			THROW 51000, 'A calendar needs a name', 1
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1
		IF NULLIF(@ParentCalendarId, '') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Calendars (NOLOCK) WHERE id = @ParentCalendarId AND profileId = @ProfileId)
			THROW 51000, 'The calendar it takes after is not in this profile', 1
		IF @ParentCalendarId = @CalendarId
			THROW 51000, 'A calendar can''t take after itself', 1
		IF ISJSON(ISNULL(@Shifts, '[]')) = 0 OR ISJSON(ISNULL(@Exceptions, '[]')) = 0
			THROW 51000, 'The shifts and exceptions need to be json lists', 1
		IF EXISTS (SELECT 1 FROM OPENJSON(@Exceptions) WITH (startTime DATETIME2(0), endTime DATETIME2(0), kind VARCHAR(20))
				WHERE startTime IS NULL OR endTime IS NULL OR endTime <= startTime OR ISNULL(kind, 'Down') NOT IN ('Down', 'Available'))
			THROW 51000, 'Every exception needs a start before its end, and a kind of Down or Available', 1
		IF EXISTS (SELECT 1 FROM OPENJSON(@Shifts) WITH ([dayOfWeek] INT, startTime TIME(0), endTime TIME(0))
				WHERE [dayOfWeek] NOT BETWEEN 0 AND 6 OR startTime IS NULL OR endTime IS NULL)
			THROW 51000, 'Every shift needs a day from 0 (Sunday) to 6 and a start and end time', 1

		BEGIN TRANSACTION
			IF @CalendarId IS NULL
			BEGIN
				SET @CalendarId = CAST(NEWID() AS VARCHAR(50))
				INSERT INTO Calendars (id, profileId, [name], parentCalendarId, resourceEntityTypeId, resourceKey)
					VALUES (@CalendarId, @ProfileId, @Name, NULLIF(@ParentCalendarId, ''), NULLIF(@ResourceEntityTypeId, ''), NULLIF(@ResourceKey, ''))
			END
			ELSE
			BEGIN
				UPDATE Calendars
					SET [name] = @Name, parentCalendarId = NULLIF(@ParentCalendarId, ''), resourceEntityTypeId = NULLIF(@ResourceEntityTypeId, ''), resourceKey = NULLIF(@ResourceKey, '')
					WHERE id = @CalendarId AND profileId = @ProfileId
				IF @@ROWCOUNT = 0
					THROW 51000, 'Calendar not found', 1
			END
			DELETE FROM CalendarShifts WHERE calendarId = @CalendarId
			INSERT INTO CalendarShifts (calendarId, [dayOfWeek], startTime, endTime)
				SELECT @CalendarId, s.[dayOfWeek], s.startTime, s.endTime
					FROM OPENJSON(ISNULL(@Shifts, '[]')) WITH ([dayOfWeek] TINYINT, startTime TIME(0), endTime TIME(0)) s
			DELETE FROM CalendarExceptions WHERE calendarId = @CalendarId
			INSERT INTO CalendarExceptions (calendarId, startTime, endTime, kind, reason)
				SELECT @CalendarId, e.startTime, e.endTime, ISNULL(e.kind, 'Down'), NULLIF(e.reason, '')
					FROM OPENJSON(ISNULL(@Exceptions, '[]')) WITH (startTime DATETIME2(0), endTime DATETIME2(0), kind VARCHAR(20), reason NVARCHAR(500)) e
		COMMIT TRANSACTION

		SELECT dbo.GetCalendarJson(@CalendarId) [results]
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

/****** Object:  StoredProcedure [dbo].[DeleteCalendar] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes a calendar, the ones that took after it take after its parent instead
-- EXEC DeleteCalendar '<profile id>', '<calendar id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteCalendar]
	@ProfileId VARCHAR(50),
	@CalendarId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		DECLARE @parentId VARCHAR(50) = (SELECT parentCalendarId FROM Calendars (NOLOCK) WHERE id = @CalendarId AND profileId = @ProfileId)
		IF NOT EXISTS (SELECT 1 FROM Calendars (NOLOCK) WHERE id = @CalendarId AND profileId = @ProfileId)
			THROW 51000, 'Calendar not found', 1

		BEGIN TRANSACTION
			UPDATE Calendars SET parentCalendarId = @parentId WHERE parentCalendarId = @CalendarId
			DELETE FROM Calendars WHERE id = @CalendarId
		COMMIT TRANSACTION

		SELECT (SELECT @CalendarId [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
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

/****** Object:  StoredProcedure [dbo].[GetRules] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- A profile's rules in their order
-- EXEC GetRules '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetRules]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		SELECT ISNULL(
			(SELECT '[' + STRING_AGG(CAST(dbo.GetRuleJson(r.id) AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY r.sortOrder, r.name) + ']'
				FROM Rules r (NOLOCK)
				WHERE r.profileId = @ProfileId), '[]') [results]
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

/****** Object:  StoredProcedure [dbo].[SaveRule] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds a rule, or changes one, blocks is the rule builder's json { who : { entity, when }, params : { ... } }
-- EXEC SaveRule '<profile id>', NULL, 'Tools fit the press', 'Condition', 1, 'Hard', 0, '{"params":{"condition":"tooling.size <= resource.max_tool_size"}}', 10
CREATE OR ALTER PROCEDURE [dbo].[SaveRule]
	@ProfileId VARCHAR(50),
	@RuleId VARCHAR(50) = NULL,
	@Name NVARCHAR(200),
	@RuleTypeId VARCHAR(50),
	@Enabled BIT = 1,
	@Strength VARCHAR(10) = 'Hard',
	@Weight INT = 0,
	@Blocks NVARCHAR(MAX) = '{}',
	@SortOrder INT = NULL
AS
BEGIN
	BEGIN TRY
		SET @Name = LTRIM(RTRIM(ISNULL(@Name, '')))
		IF @Name = ''
			THROW 51000, 'A rule needs a name', 1
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1
		IF NOT EXISTS (SELECT 1 FROM RuleTypes (NOLOCK) WHERE id = @RuleTypeId)
			THROW 51000, 'Unknown rule type', 1
		IF ISNULL(@Strength, '') NOT IN ('Hard', 'Soft')
			THROW 51000, 'The strength needs to be Hard or Soft', 1
		IF @Strength = 'Hard' AND EXISTS (SELECT 1 FROM RuleTypes (NOLOCK) WHERE id = @RuleTypeId AND supportsHard = 0)
			THROW 51000, 'This kind of rule can only be soft', 1
		IF @Strength = 'Soft' AND EXISTS (SELECT 1 FROM RuleTypes (NOLOCK) WHERE id = @RuleTypeId AND supportsSoft = 0)
			THROW 51000, 'This kind of rule can only be hard', 1
		IF ISJSON(ISNULL(@Blocks, '{}')) = 0
			THROW 51000, 'The rule''s blocks need to be json', 1

		IF @RuleId IS NULL
		BEGIN
			SET @RuleId = CAST(NEWID() AS VARCHAR(50))
			INSERT INTO Rules (id, profileId, [name], ruleTypeId, enabled, strength, [weight], blocks, sortOrder)
				VALUES (@RuleId, @ProfileId, @Name, @RuleTypeId, ISNULL(@Enabled, 1), @Strength, ISNULL(@Weight, 0), ISNULL(@Blocks, '{}'),
					ISNULL(@SortOrder, (SELECT ISNULL(MAX(sortOrder), 0) + 10 FROM Rules (NOLOCK) WHERE profileId = @ProfileId)))
		END
		ELSE
		BEGIN
			UPDATE Rules
				SET [name] = @Name, ruleTypeId = @RuleTypeId, enabled = ISNULL(@Enabled, 1), strength = @Strength, [weight] = ISNULL(@Weight, 0),
					blocks = ISNULL(@Blocks, '{}'), sortOrder = ISNULL(@SortOrder, sortOrder), modified = SYSUTCDATETIME()
				WHERE id = @RuleId AND profileId = @ProfileId
			IF @@ROWCOUNT = 0
				THROW 51000, 'Rule not found', 1
		END

		SELECT dbo.GetRuleJson(@RuleId) [results]
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

/****** Object:  StoredProcedure [dbo].[DeleteRule] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Removes a rule
-- EXEC DeleteRule '<profile id>', '<rule id>'
CREATE OR ALTER PROCEDURE [dbo].[DeleteRule]
	@ProfileId VARCHAR(50),
	@RuleId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		DELETE FROM Rules WHERE id = @RuleId AND profileId = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Rule not found', 1

		SELECT (SELECT @RuleId [id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) [results]
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

/****** Object:  StoredProcedure [dbo].[SaveProfileSettings] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- The profile's solve settings, json { horizonDays, timeLimitSeconds, allowUnscheduled, colourBy, labelBy, ... }
-- EXEC SaveProfileSettings '<profile id>', '{"horizonDays":28,"timeLimitSeconds":30}'
CREATE OR ALTER PROCEDURE [dbo].[SaveProfileSettings]
	@ProfileId VARCHAR(50),
	@Settings NVARCHAR(MAX)
AS
BEGIN
	BEGIN TRY
		IF ISJSON(ISNULL(@Settings, '')) = 0
			THROW 51000, 'The settings need to be json', 1
		UPDATE Profiles SET settings = @Settings, modified = SYSUTCDATETIME() WHERE id = @ProfileId
		IF @@ROWCOUNT = 0
			THROW 51000, 'Profile not found', 1

		SELECT dbo.GetProfileJson(@ProfileId) [results]
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

/****** Object:  StoredProcedure [dbo].[GetProfileModel] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Everything the rule engine needs from SchedulerMeta in one go: entities with their sources and attributes,
-- the value scales, relationships with their typed in values and sources, calendars and the enabled rules
-- the API reads the entity rows and live relationship values from the sources
-- EXEC GetProfileModel '<profile id>'
CREATE OR ALTER PROCEDURE [dbo].[GetProfileModel]
	@ProfileId VARCHAR(50)
AS
BEGIN
	BEGIN TRY
		IF NOT EXISTS (SELECT 1 FROM Profiles (NOLOCK) WHERE id = @ProfileId)
			THROW 51000, 'Profile not found', 1

		SELECT (
			SELECT p.id [profileId], p.name, p.timeUnitMinutes, JSON_QUERY(p.settings) [settings],
				JSON_QUERY(ISNULL((SELECT e.id, e.name, e.kind, e.durationFormula, e.sourceId,
						JSON_QUERY(ISNULL((SELECT a.name, a.dataType, a.[role], a.sourceColumn, r.name [referencesEntity]
							FROM Attributes a (NOLOCK)
								LEFT JOIN EntityTypes r (NOLOCK) ON r.id = a.referencesEntityTypeId
							WHERE a.entityTypeId = e.id
							ORDER BY a.sortOrder, a.name
							FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [attributes]
					FROM EntityTypes e (NOLOCK)
					WHERE e.profileId = p.id
					FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [entities],
				JSON_QUERY(ISNULL((SELECT s.id, s.name,
						JSON_QUERY(ISNULL((SELECT v.code, v.label, v.meaning, v.[weight]
							FROM ValueScaleValues v (NOLOCK)
							WHERE v.valueScaleId = s.id
							ORDER BY v.sortOrder
							FOR JSON PATH), '[]')) [values]
					FROM ValueScales s (NOLOCK)
					WHERE s.profileId IS NULL OR s.profileId = p.id
					FOR JSON PATH), '[]')) [valueScales],
				JSON_QUERY(ISNULL((SELECT r.id, r.name, a.name [entityA], b.name [entityB], r.valueScaleId, r.defaultValue, r.[symmetric],
						r.sourceId, r.sourceAColumn, r.sourceBColumn, r.sourceValueColumn,
						JSON_QUERY(ISNULL((SELECT v.aKey [a], v.bKey [b], v.[value]
							FROM RelationshipValues v (NOLOCK)
							WHERE v.relationshipId = r.id
							FOR JSON PATH), '[]')) [values]
					FROM Relationships r (NOLOCK)
						JOIN EntityTypes a (NOLOCK) ON a.id = r.entityTypeAId
						JOIN EntityTypes b (NOLOCK) ON b.id = r.entityTypeBId
					WHERE r.profileId = p.id
					FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [relationships],
				JSON_QUERY(ISNULL((SELECT c.id, c.name, c.parentCalendarId [parentId], e.name [resourceEntity], c.resourceKey,
						JSON_QUERY(ISNULL((SELECT x.[dayOfWeek], CONVERT(VARCHAR(8), x.startTime, 108) [startTime], CONVERT(VARCHAR(8), x.endTime, 108) [endTime]
							FROM CalendarShifts x (NOLOCK)
							WHERE x.calendarId = c.id
							FOR JSON PATH), '[]')) [shifts],
						JSON_QUERY(ISNULL((SELECT x.startTime, x.endTime, x.kind, x.reason
							FROM CalendarExceptions x (NOLOCK)
							WHERE x.calendarId = c.id
							FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [exceptions]
					FROM Calendars c (NOLOCK)
						LEFT JOIN EntityTypes e (NOLOCK) ON e.id = c.resourceEntityTypeId
					WHERE c.profileId = p.id
					FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [calendars],
				JSON_QUERY(ISNULL((SELECT r.id, r.name, r.ruleTypeId, r.enabled, r.strength, r.[weight], JSON_QUERY(r.blocks) [blocks]
					FROM Rules r (NOLOCK)
					WHERE r.profileId = p.id
					ORDER BY r.sortOrder, r.name
					FOR JSON PATH), '[]')) [rules],
				JSON_QUERY(ISNULL((SELECT s.id, s.connectionId, s.kind, s.[schema], s.objectName, s.queryText
					FROM Sources s (NOLOCK)
					WHERE s.profileId = p.id
					FOR JSON PATH, INCLUDE_NULL_VALUES), '[]')) [sources]
				FROM Profiles p (NOLOCK)
				WHERE p.id = @ProfileId
				FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) [results]
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
