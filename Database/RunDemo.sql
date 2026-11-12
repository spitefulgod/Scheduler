-- Adds the demo source data and the Press Scheduling profile, safe to run again
--   sqlcmd -S PLEXSERVER -E -I -b -v DatabaseName=SchedulerMeta -i RunDemo.sql
:on error exit
:r 08_DemoSource.sql
:r 09_Seed_PressScheduling.sql
