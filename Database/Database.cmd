@echo off
rem Builds the SchedulerMeta database on a server, or brings an existing one up to date
rem
rem   Database.cmd <server> [database] [/user <login>] [/demodata]
rem
rem   Database.cmd PLEXSERVER SchedulerMeta /demodata      development, with the demo press data and the Press Scheduling profile
rem   Database.cmd sql01 SchedulerMeta /user scheduler      another server with a SQL Server login, Windows sign in without /user
rem
rem A database that isn't there yet is built with RunAll.sql, and every script in Updates is recorded as run, as the build
rem scripts are always kept up to date. An existing database gets the Updates scripts it hasn't had yet, in number order,
rem each recorded in SchemaVersions once it has run, stopping at the first that fails. The functions and procedures are then
rem put in again, they're all CREATE OR ALTER so they always match the scripts.
rem With /user the password is asked for, or taken from SQLCMDPASSWORD when it's already set. Needs sqlcmd.

setlocal EnableDelayedExpansion
cd /d "%~dp0"
if "%~1"=="" goto usage
set "SERVER=%~1"
set "DATABASE=SchedulerMeta"
set "SQLUSER="
set "DEMO=0"
shift

:args
if "%~1"=="" goto start
if /i "%~1"=="/demodata" goto argDemo
if /i "%~1"=="/user" goto argUser
set "DATABASE=%~1"
shift
goto args
:argDemo
set "DEMO=1"
shift
goto args
:argUser
if "%~2"=="" goto usage
set "SQLUSER=%~2"
shift
shift
goto args

:start
rem Windows sign in, or a SQL Server login whose password goes through SQLCMDPASSWORD so it isn't on the command line
set "SIGNIN=-E"
if not defined SQLUSER goto signedIn
set SIGNIN=-U "%SQLUSER%"
if defined SQLCMDPASSWORD goto signedIn
set /p "SQLCMDPASSWORD=Password for %SQLUSER%: "
:signedIn
set "SQLCMD=sqlcmd -S "%SERVER%" %SIGNIN% -I -b"
set "QUERYFILE=%TEMP%\scheduler_database_%RANDOM%.sql"

set "Q=SELECT CASE WHEN DB_ID(N'%DATABASE%') IS NULL THEN 0 ELSE 1 END"
call :query master
if not defined RESULT goto unreachable
if "%RESULT%"=="0" goto build
goto update

rem A new database is built as it is now, so every update is already in it
:build
echo %DATABASE% isn't on %SERVER%, building it
call :run "RunAll.sql" || goto failed
for /f "delims=" %%f in ('dir /b /on Updates\*.sql') do (
    call :record "%%~nf" || goto failed
)
goto demo

rem An existing database gets the updates it hasn't had, update 0001 adds SchemaVersions so before it nothing is recorded
:update
echo %DATABASE% is on %SERVER%, bringing it up to date
set "Q=SELECT CASE WHEN OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL THEN 0 ELSE 1 END"
call :query %DATABASE%
set "HASVERSIONS=%RESULT%"
set "RAN=0"
for /f "delims=" %%f in ('dir /b /on Updates\*.sql') do (
    call :updateOne "%%~nf" || goto failed
)
if "%RAN%"=="0" echo No updates to run
rem the functions, procedures and built in lookup data are put in again so they always match the scripts
for %%f in (02_Functions.sql 03_Procedures_Profiles.sql 04_Procedures_Connections.sql 05_Procedures_Sources.sql 06_Procedures_Catalogue.sql 10_Procedures_Model.sql 11_Procedures_Schedule.sql 07_Seed_Catalogue.sql) do (
    call :run "%%f" || goto failed
)

:demo
if "%DEMO%"=="1" call :run "RunDemo.sql" || goto failed
echo %DATABASE% on %SERVER% is up to date
goto done

rem Runs one update script unless it has run already, e.g. 0002_AddCalendarColour is version 2
:updateOne
call :version "%~1"
if not "%HASVERSIONS%"=="1" goto updateRun
set "Q=SELECT COUNT(*) FROM SchemaVersions WHERE version = !VERSION!"
call :query %DATABASE%
if not "%RESULT%"=="0" exit /b 0
:updateRun
call :run "Updates\%~1.sql" || exit /b 1
set "HASVERSIONS=1"
set "RAN=1"
call :record "%~1"
exit /b %ERRORLEVEL%

rem Notes an update as run
:record
call :version "%~1"
set "Q=INSERT INTO SchemaVersions (version, name) VALUES (!VERSION!, N'%~1')"
call :exec %DATABASE%
exit /b %ERRORLEVEL%

rem The number at the start of an update's name, they're always four digits so 0008 isn't read as octal
:version
for /f "tokens=1 delims=_" %%v in ("%~1") do set /a "VERSION=1%%v-10000"
exit /b 0

rem Runs a script file, the scripts use $(DatabaseName) so it is passed in
:run
echo Running %~nx1
pushd "%~dp1"
%SQLCMD% -v DatabaseName=%DATABASE% -i "%~nx1" > nul
set "CODE=%ERRORLEVEL%"
popd
if not "%CODE%"=="0" (
    echo %~nx1 failed, nothing after it has been run, run it on its own to see the error:
    echo   sqlcmd -S "%SERVER%" %SIGNIN% -I -b -v DatabaseName=%DATABASE% -i "%~f1"
    exit /b 1
)
exit /b 0

rem Runs the query in Q against a database and puts the first value back in RESULT
:query
set "RESULT="
> "%QUERYFILE%" echo SET NOCOUNT ON; !Q!
for /f "usebackq delims=" %%a in (`%SQLCMD% -d %~1 -h -1 -W -i "%QUERYFILE%"`) do if not defined RESULT set "RESULT=%%a"
exit /b 0

rem Runs the statement in Q against a database
:exec
> "%QUERYFILE%" echo SET NOCOUNT ON; !Q!
%SQLCMD% -d %~1 -i "%QUERYFILE%" > nul
exit /b %ERRORLEVEL%

:unreachable
echo Couldn't reach %SERVER%, check the server name and sign in
goto failed

:usage
echo Database.cmd ^<server^> [database] [/user ^<login^>] [/demodata]
echo   Builds the SchedulerMeta database, unless another name is given, or brings an existing one up to date
exit /b 1

:failed
if defined QUERYFILE del "%QUERYFILE%" 2> nul
exit /b 1

:done
if defined QUERYFILE del "%QUERYFILE%" 2> nul
exit /b 0
