/****** Object:  Database [SchedulerMeta] ******/
USE [master]
GO

-- Creates the SchedulerMeta database when it does not already exist
IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
	CREATE DATABASE [$(DatabaseName)]
END
GO
