-- Builds the whole database, tables then functions, procedures and the built in lookup data
-- Database.cmd is the usual way in, it runs this for a new database and the Updates folder for an existing one
-- run on its own from this folder, e.g.
--   sqlcmd -S PLEXSERVER -E -I -b -v DatabaseName=SchedulerMeta -i RunAll.sql
-- then 08_DemoSource.sql and 09_Seed_PressScheduling.sql add the demo data and the Press Scheduling profile
:on error exit
:r 00_CreateDatabase.sql
:r 01_Tables.sql
:r 02_Functions.sql
:r 03_Procedures_Profiles.sql
:r 04_Procedures_Connections.sql
:r 05_Procedures_Sources.sql
:r 06_Procedures_Catalogue.sql
:r 10_Procedures_Model.sql
:r 11_Procedures_Schedule.sql
:r 12_Procedures_Security.sql
:r 07_Seed_Catalogue.sql
