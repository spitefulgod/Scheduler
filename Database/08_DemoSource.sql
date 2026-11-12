/****** Object:  Demo source data in the src schema ******/
USE [$(DatabaseName)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Made up press shop data for the Press Scheduling demo, it plays the part of a factory system
-- the scheduler only ever reads it through the DEMO connection, the same as any other source
-- The rows are worked out from row numbers so every run gives the same data, tables are only filled when empty

IF SCHEMA_ID(N'src') IS NULL
	EXEC(N'CREATE SCHEMA [src]')
GO

-- Rubber compounds
IF OBJECT_ID(N'src.Compound', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[Compound](
		[CompoundCode] NVARCHAR(20) NOT NULL,
		[Description] NVARCHAR(100) NOT NULL,
		[Family] NVARCHAR(10) NOT NULL,
		[CureTempC] INT NOT NULL,
		CONSTRAINT [PK_Compound] PRIMARY KEY CLUSTERED ([CompoundCode])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.Compound)
	INSERT INTO src.Compound (CompoundCode, [Description], Family, CureTempC) VALUES
		(N'NBR-70', N'Nitrile 70 Shore A', N'NBR', 170),
		(N'EPDM-60', N'EPDM 60 Shore A', N'EPDM', 175),
		(N'FKM-75', N'Fluoroelastomer 75 Shore A', N'FKM', 185),
		(N'SIL-50', N'Silicone 50 Shore A', N'SIL', 165),
		(N'NR-55', N'Natural rubber 55 Shore A', N'NR', 150),
		(N'CR-65', N'Neoprene 65 Shore A', N'CR', 160),
		(N'HNBR-80', N'Hydrogenated nitrile 80', N'HNBR', 180),
		(N'ACM-70', N'Polyacrylate 70 Shore A', N'ACM', 175)
GO

-- Presses, PlatenSlots is how many tools one can run at once
IF OBJECT_ID(N'src.Press', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[Press](
		[PressCode] NVARCHAR(20) NOT NULL,
		[Description] NVARCHAR(100) NOT NULL,
		[PlatenSlots] INT NOT NULL,
		[MaxToolSize] INT NOT NULL,
		[Tonnage] INT NOT NULL,
		CONSTRAINT [PK_Press] PRIMARY KEY CLUSTERED ([PressCode])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.Press)
	INSERT INTO src.Press (PressCode, [Description], PlatenSlots, MaxToolSize, Tonnage) VALUES
		(N'P01', N'Press 01 - 100t single', 1, 2, 100),
		(N'P02', N'Press 02 - 100t single', 1, 2, 100),
		(N'P03', N'Press 03 - 200t twin', 2, 3, 200),
		(N'P04', N'Press 04 - 200t twin', 2, 3, 200),
		(N'P05', N'Press 05 - 250t twin', 2, 4, 250),
		(N'P06', N'Press 06 - 250t twin', 2, 4, 250),
		(N'P07', N'Press 07 - 300t triple', 3, 4, 300),
		(N'P08', N'Press 08 - 400t triple', 3, 5, 400),
		(N'P09', N'Press 09 - 400t single', 1, 5, 400),
		(N'P10', N'Press 10 - 150t twin', 2, 2, 150)
GO

-- Mould tools, Copies is how many of the same tool there are
IF OBJECT_ID(N'src.Tool', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[Tool](
		[ToolCode] NVARCHAR(20) NOT NULL,
		[Description] NVARCHAR(100) NOT NULL,
		[Size] INT NOT NULL,
		[Copies] INT NOT NULL,
		[Cavities] INT NOT NULL,
		CONSTRAINT [PK_Tool] PRIMARY KEY CLUSTERED ([ToolCode])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.Tool)
BEGIN
	;WITH n AS (SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) [i] FROM sys.all_columns)
	INSERT INTO src.Tool (ToolCode, [Description], Size, Copies, Cavities)
		SELECT N'T' + RIGHT(N'000' + CAST(i AS NVARCHAR(3)), 3), N'Mould tool ' + CAST(i AS NVARCHAR(3)),
			((i * 7) % 5) + 1,
			CASE WHEN i % 6 = 0 THEN 2 WHEN i % 10 = 0 THEN 3 ELSE 1 END,
			((i * 3) % 8) + 1
			FROM n
END
GO

-- Jobs to schedule, each runs one compound in one tool
IF OBJECT_ID(N'src.Job', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[Job](
		[JobNo] NVARCHAR(20) NOT NULL,
		[CompoundCode] NVARCHAR(20) NOT NULL,
		[ToolCode] NVARCHAR(20) NOT NULL,
		[Quantity] INT NOT NULL,
		[CycleSecs] INT NOT NULL,
		[ReleaseDate] DATETIME2(0) NOT NULL,
		[DueDate] DATETIME2(0) NOT NULL,
		[Priority] INT NOT NULL,
		CONSTRAINT [PK_Job] PRIMARY KEY CLUSTERED ([JobNo]),
		CONSTRAINT [FK_Job_Compound] FOREIGN KEY ([CompoundCode]) REFERENCES [src].[Compound]([CompoundCode]),
		CONSTRAINT [FK_Job_Tool] FOREIGN KEY ([ToolCode]) REFERENCES [src].[Tool]([ToolCode])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.Job)
BEGIN
	;WITH n AS (SELECT TOP (150) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) [i] FROM sys.all_columns),
		c AS (SELECT CompoundCode, ROW_NUMBER() OVER (ORDER BY CompoundCode) [k] FROM src.Compound)
	INSERT INTO src.Job (JobNo, CompoundCode, ToolCode, Quantity, CycleSecs, ReleaseDate, DueDate, [Priority])
		SELECT N'J' + RIGHT(N'0000' + CAST(n.i AS NVARCHAR(4)), 4), c.CompoundCode,
			N'T' + RIGHT(N'000' + CAST(((n.i * 11) % 30) + 1 AS NVARCHAR(3)), 3),
			200 + ((n.i * 37) % 20) * 50,
			60 + ((n.i * 13) % 10) * 30,
			DATEADD(HOUR, 6, DATEADD(DAY, n.i % 10, CAST(CAST(GETDATE() AS DATE) AS DATETIME2(0)))),
			DATEADD(HOUR, 22, DATEADD(DAY, (n.i % 10) + 3 + (n.i % 7), CAST(CAST(GETDATE() AS DATE) AS DATETIME2(0)))),
			1 + (n.i % 5)
			FROM n
				JOIN c ON c.k = ((n.i * 7) % 8) + 1
END
GO

-- Other tools a job can run in, about one job in three has one
IF OBJECT_ID(N'src.JobAlternativeTool', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[JobAlternativeTool](
		[JobNo] NVARCHAR(20) NOT NULL,
		[ToolCode] NVARCHAR(20) NOT NULL,
		CONSTRAINT [PK_JobAlternativeTool] PRIMARY KEY CLUSTERED ([JobNo], [ToolCode]),
		CONSTRAINT [FK_JobAlternativeTool_Job] FOREIGN KEY ([JobNo]) REFERENCES [src].[Job]([JobNo]),
		CONSTRAINT [FK_JobAlternativeTool_Tool] FOREIGN KEY ([ToolCode]) REFERENCES [src].[Tool]([ToolCode])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.JobAlternativeTool)
	INSERT INTO src.JobAlternativeTool (JobNo, ToolCode)
		SELECT j.JobNo, a.ToolCode
			FROM src.Job j
				CROSS APPLY (SELECT N'T' + RIGHT(N'000' + CAST(((CAST(SUBSTRING(j.JobNo, 2, 4) AS INT) * 11 + 15) % 30) + 1 AS NVARCHAR(3)), 3) [ToolCode]) a
			WHERE CAST(SUBSTRING(j.JobNo, 2, 4) AS INT) % 3 = 0
				AND a.ToolCode <> j.ToolCode
GO

-- How well each compound suits each press, P preferred, A acceptable, V avoid, X not allowed
IF OBJECT_ID(N'src.CompoundPress', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[CompoundPress](
		[CompoundCode] NVARCHAR(20) NOT NULL,
		[PressCode] NVARCHAR(20) NOT NULL,
		[Grade] NCHAR(1) NOT NULL,
		CONSTRAINT [PK_CompoundPress] PRIMARY KEY CLUSTERED ([CompoundCode], [PressCode]),
		CONSTRAINT [FK_CompoundPress_Compound] FOREIGN KEY ([CompoundCode]) REFERENCES [src].[Compound]([CompoundCode]),
		CONSTRAINT [FK_CompoundPress_Press] FOREIGN KEY ([PressCode]) REFERENCES [src].[Press]([PressCode])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.CompoundPress)
BEGIN
	;WITH c AS (SELECT CompoundCode, ROW_NUMBER() OVER (ORDER BY CompoundCode) [k] FROM src.Compound),
		p AS (SELECT PressCode, ROW_NUMBER() OVER (ORDER BY PressCode) [k] FROM src.Press)
	INSERT INTO src.CompoundPress (CompoundCode, PressCode, Grade)
		SELECT c.CompoundCode, p.PressCode,
			CASE (c.k * 3 + p.k) % 7 WHEN 0 THEN N'X' WHEN 1 THEN N'V' WHEN 2 THEN N'A' WHEN 3 THEN N'A' ELSE N'P' END
			FROM c
				CROSS JOIN p
END
GO

-- Which compounds can run at the same time on one press, each pair is held once with CompoundA before CompoundB
IF OBJECT_ID(N'src.CompoundCompatibility', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[CompoundCompatibility](
		[CompoundA] NVARCHAR(20) NOT NULL,
		[CompoundB] NVARCHAR(20) NOT NULL,
		[Compatible] NCHAR(1) NOT NULL,
		CONSTRAINT [PK_CompoundCompatibility] PRIMARY KEY CLUSTERED ([CompoundA], [CompoundB]),
		CONSTRAINT [FK_CompoundCompatibility_CompoundA] FOREIGN KEY ([CompoundA]) REFERENCES [src].[Compound]([CompoundCode]),
		CONSTRAINT [FK_CompoundCompatibility_CompoundB] FOREIGN KEY ([CompoundB]) REFERENCES [src].[Compound]([CompoundCode])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.CompoundCompatibility)
	INSERT INTO src.CompoundCompatibility (CompoundA, CompoundB, Compatible)
		SELECT a.CompoundCode, b.CompoundCode,
			-- silicone spoils everything else, and platens can't be more than 15 degrees apart
			CASE WHEN a.Family = N'SIL' OR b.Family = N'SIL' THEN N'N'
				WHEN ABS(a.CureTempC - b.CureTempC) > 15 THEN N'N'
				ELSE N'Y' END
			FROM src.Compound a
				JOIN src.Compound b ON a.CompoundCode < b.CompoundCode
GO

-- Which presses a tool fits, from the tool's size and the biggest the press takes
CREATE OR ALTER VIEW [src].[ToolPressFit]
AS
	SELECT t.ToolCode, p.PressCode, CAST(CASE WHEN t.Size <= p.MaxToolSize THEN N'Y' ELSE N'N' END AS NCHAR(1)) [Fits]
		FROM src.Tool t
			CROSS JOIN src.Press p
GO

/****** The extra demo data for the moulding rules: cells, sizes, inspections and splitting ******/
-- Added to tables made by an earlier version of this script as well, the values are worked out from the codes so they're always the same

-- Presses sit in cells, have platen space in tool size units and run tools of three size classes
IF COL_LENGTH(N'src.Press', N'Cell') IS NULL
	ALTER TABLE src.Press ADD [Cell] NVARCHAR(10) NULL, [PlatenSpace] INT NULL
GO
UPDATE src.Press
	SET Cell = CASE WHEN CAST(SUBSTRING(PressCode, 2, 2) AS INT) <= 5 THEN N'A' ELSE N'B' END,
		PlatenSpace = PlatenSlots * MaxToolSize
	WHERE Cell IS NULL OR PlatenSpace IS NULL
GO

-- Tools have a size class and take time to change
IF COL_LENGTH(N'src.Tool', N'SizeClass') IS NULL
	ALTER TABLE src.Tool ADD [SizeClass] NVARCHAR(10) NULL, [ChangeMinutes] INT NULL
GO
UPDATE src.Tool
	SET SizeClass = CASE WHEN Size <= 2 THEN N'S' WHEN Size <= 4 THEN N'M' ELSE N'L' END,
		ChangeMinutes = 30 + Size * 15
	WHERE SizeClass IS NULL OR ChangeMinutes IS NULL
GO

-- Jobs have a cell, a customer, an inspection every N cycles and may split across presses at once
IF COL_LENGTH(N'src.Job', N'Cell') IS NULL
	ALTER TABLE src.Job ADD [Cell] NVARCHAR(10) NULL, [Customer] NVARCHAR(50) NULL, [InspectEvery] INT NULL, [AllowParallel] BIT NULL
GO
UPDATE src.Job
	SET Cell = CASE WHEN CAST(SUBSTRING(JobNo, 2, 4) AS INT) % 2 = 0 THEN N'A' ELSE N'B' END,
		Customer = CASE CAST(SUBSTRING(JobNo, 2, 4) AS INT) % 4 WHEN 0 THEN N'Aero' WHEN 1 THEN N'Auto' WHEN 2 THEN N'Medical' ELSE N'General' END,
		InspectEvery = CASE WHEN CAST(SUBSTRING(JobNo, 2, 4) AS INT) % 4 = 2 THEN 25 ELSE 100 END,
		AllowParallel = CASE WHEN CAST(SUBSTRING(JobNo, 2, 4) AS INT) % 4 = 2 THEN 0 ELSE 1 END
	WHERE Cell IS NULL
GO

-- A demo built a while ago has its jobs moved on so they start today, the jobs keep their spacing
DECLARE @shift INT = (SELECT DATEDIFF(DAY, MIN(ReleaseDate), CAST(GETDATE() AS DATE)) FROM src.Job)
IF @shift > 0 AND (SELECT MAX(DueDate) FROM src.Job) < GETDATE()
	UPDATE src.Job SET ReleaseDate = DATEADD(DAY, @shift, ReleaseDate), DueDate = DATEADD(DAY, @shift, DueDate)
GO

-- The tool size classes
IF OBJECT_ID(N'src.ToolSizeClass', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[ToolSizeClass](
		[SizeClass] NVARCHAR(10) NOT NULL,
		[Description] NVARCHAR(100) NOT NULL,
		CONSTRAINT [PK_ToolSizeClass] PRIMARY KEY CLUSTERED ([SizeClass])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.ToolSizeClass)
	INSERT INTO src.ToolSizeClass (SizeClass, [Description]) VALUES (N'S', N'Small, sizes 1 and 2'), (N'M', N'Medium, sizes 3 and 4'), (N'L', N'Large, size 5')
GO

-- How many tools of each size class a press's platen configuration takes, e.g. 6 small or 2 medium, none means it can't take that size
IF OBJECT_ID(N'src.PressSizeSlots', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[PressSizeSlots](
		[PressCode] NVARCHAR(20) NOT NULL,
		[SizeClass] NVARCHAR(10) NOT NULL,
		[Slots] INT NOT NULL,
		CONSTRAINT [PK_PressSizeSlots] PRIMARY KEY CLUSTERED ([PressCode], [SizeClass])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.PressSizeSlots)
	INSERT INTO src.PressSizeSlots (PressCode, SizeClass, Slots)
		SELECT p.PressCode, c.SizeClass,
			CASE c.SizeClass WHEN N'S' THEN p.PlatenSlots * 2 WHEN N'M' THEN p.PlatenSlots ELSE CASE WHEN p.MaxToolSize >= 5 THEN 1 ELSE 0 END END
			FROM src.Press p
				CROSS JOIN src.ToolSizeClass c
			WHERE c.SizeClass <> N'L' OR p.MaxToolSize >= 5
GO


-- Platen combinations: each is a set of tools of each size class the platen can hold at once, e.g. 1 medium and 3 small,
-- a press can be set up in the combinations for its number of slots, the large ones only where it takes a size 5 tool
IF OBJECT_ID(N'src.PressConfiguration', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[PressConfiguration](
		[ConfigCode] NVARCHAR(20) NOT NULL,
		[Description] NVARCHAR(100) NOT NULL,
		[PlatenSlots] INT NOT NULL,
		CONSTRAINT [PK_PressConfiguration] PRIMARY KEY CLUSTERED ([ConfigCode])
	)
END
GO
IF OBJECT_ID(N'src.PressConfigurationSize', N'U') IS NULL
BEGIN
	CREATE TABLE [src].[PressConfigurationSize](
		[ConfigCode] NVARCHAR(20) NOT NULL,
		[SizeClass] NVARCHAR(10) NOT NULL,
		[Tools] INT NOT NULL,
		CONSTRAINT [PK_PressConfigurationSize] PRIMARY KEY CLUSTERED ([ConfigCode], [SizeClass])
	)
END
GO
IF NOT EXISTS (SELECT 1 FROM src.PressConfiguration)
BEGIN
	INSERT INTO src.PressConfiguration (ConfigCode, [Description], PlatenSlots) VALUES
		(N'1-M', N'1 medium', 1), (N'1-S', N'2 small', 1), (N'1-L', N'1 large', 1),
		(N'2-M', N'2 medium', 2), (N'2-MS', N'1 medium, 3 small', 2), (N'2-S', N'5 small', 2),
		(N'3-M', N'3 medium', 3), (N'3-MS', N'2 medium, 3 small', 3), (N'3-SM', N'1 medium, 5 small', 3), (N'3-S', N'8 small', 3), (N'3-L', N'1 large, 3 small', 3)
	INSERT INTO src.PressConfigurationSize (ConfigCode, SizeClass, Tools) VALUES
		(N'1-M', N'M', 1), (N'1-S', N'S', 2), (N'1-L', N'L', 1),
		(N'2-M', N'M', 2), (N'2-MS', N'M', 1), (N'2-MS', N'S', 3), (N'2-S', N'S', 5),
		(N'3-M', N'M', 3), (N'3-MS', N'M', 2), (N'3-MS', N'S', 3), (N'3-SM', N'M', 1), (N'3-SM', N'S', 5), (N'3-S', N'S', 8),
		(N'3-L', N'L', 1), (N'3-L', N'S', 3)
END
GO
CREATE OR ALTER VIEW [src].[PressConfigurationAllowed]
AS
	SELECT p.PressCode, c.ConfigCode
		FROM src.Press p
			JOIN src.PressConfiguration c ON c.PlatenSlots = p.PlatenSlots
		WHERE p.MaxToolSize >= 5
			OR NOT EXISTS (SELECT 1 FROM src.PressConfigurationSize s WHERE s.ConfigCode = c.ConfigCode AND s.SizeClass = N'L')
GO
