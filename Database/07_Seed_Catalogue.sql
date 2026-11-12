/****** Object:  Built in lookup data, connection types, rule blocks and value scales ******/
USE [$(DatabaseName)]
GO

-- Safe to run again, rows are added when missing and brought up to date when they are there

-- The kinds of connection, DEMO reads the src schema of this database so the demo works without a real source
MERGE ConnectionTypes t
	USING (VALUES
		('SQLSERVER', 'SQL Server'),
		('DEMO', 'Demo data (this database, src schema)')
	) s (id, [name]) ON t.id = s.id
	WHEN MATCHED THEN UPDATE SET [name] = s.[name]
	WHEN NOT MATCHED THEN INSERT (id, [name]) VALUES (s.id, s.[name]);
GO

-- The rule block catalogue, the rule engine in Scheduler.Solver handles each one by its id
-- parameters is a json list of what the block's editor asks for: { name, label, type, kind, scope, options, default, required, help }
-- type is entity, relationship, expression, condition, number, boolean or select, scope says which names an expression can use:
-- item (item.x), resource, tooling or mode (all three)
-- the blocks from the first catalogue are removed once no rule uses them
MERGE RuleTypes t
	USING (VALUES
		('RunsOn', 'Runs on', 'Assignment', 'Items run on one of the rows of a resource entity, e.g. a press, optionally only where a condition holds.',
			'optional interval per mode + exactly one mode per segment', 1, 1, '[{"name":"resource","label":"Resource entity","type":"entity","kind":"Resource","required":true,"help":"What items are scheduled on, e.g. Press"},{"name":"when","label":"Only where","type":"condition","scope":"mode","help":"e.g. resource.cell = item.cell, blank for every resource"}]', 10),
		('UsesTooling', 'Uses tooling', 'Resources', 'Items also draw copies from a pool, e.g. mould tools, with alternatives, how many run at once and the parts each makes per cycle.',
			'cumulative on the pool, capacity = copies, variable copies per segment', 1, 0, '[{"name":"tooling","label":"Tooling entity","type":"entity","kind":"Resource","required":true,"help":"e.g. Tool"},{"name":"toolAttribute","label":"Tool of the item","type":"expression","scope":"item","help":"e.g. item.tool"},{"name":"alternativesRelationshipId","label":"Alternative tools","type":"relationship","help":"An Item x Tool list of other tools it can use"},{"name":"minCount","label":"Fewest copies at once","type":"expression","scope":"mode","default":"1"},{"name":"maxCount","label":"Most copies at once","type":"expression","scope":"mode","default":"1","help":"e.g. tooling.copies to use every copy there is"},{"name":"unitsPerCycle","label":"Parts per cycle per copy","type":"expression","scope":"mode","default":"1","help":"e.g. tooling.cavities for multi cavity tools"},{"name":"copies","label":"Copies of each tool","type":"expression","scope":"tooling","help":"Defaults to the Copies attribute"},{"name":"cycleSeconds","label":"Cycle seconds with this tool","type":"expression","scope":"mode","help":"Blank uses the item''s cycle time"}]', 20),
		('Allowed', 'Allowed by matrix', 'Assignment', 'A relationship decides which item, resource and tool combinations are allowed, preferred, avoided or forbidden.',
			'modes left out (hard) or cost their scale weight (soft)', 1, 1, '[{"name":"relationshipId","label":"Relationship","type":"relationship","required":true},{"name":"a","label":"A side is","type":"expression","scope":"mode","help":"e.g. item.compound, blank works it out"},{"name":"b","label":"B side is","type":"expression","scope":"mode","help":"e.g. resource, blank works it out"}]', 30),
		('Condition', 'Condition', 'Assignment', 'An expression over item, resource and tooling that must (or should) hold, e.g. tooling.size <= resource.max_tool_size or item.cell = resource.cell.',
			'modes left out (hard) or cost the rule weight (soft)', 1, 1, '[{"name":"condition","label":"Condition","type":"condition","scope":"mode","required":true}]', 40),
		('Capacity', 'Capacity', 'Resources', 'A capacity on each resource, e.g. platen space, and what each tool copy or item takes of it, so different sized tools from different jobs can share a resource.',
			'cumulative per resource dimension', 1, 0, '[{"name":"capacity","label":"Capacity of the resource","type":"expression","scope":"resource","required":true,"help":"e.g. resource.platen_space"},{"name":"demand","label":"Each takes","type":"expression","scope":"mode","default":"1","help":"e.g. tooling.size"},{"name":"per","label":"Per","type":"select","options":["copy","item"],"default":"copy","help":"Each tool copy, or each item whatever its copies"}]', 50),
		('CapacityMatrix', 'Capacity by size', 'Resources', 'A resource x size matrix of how many of each fit, e.g. 7 small, 2 medium or 1 large, for configurations that aren''t a simple total.',
			'cumulative per resource and size', 1, 0, '[{"name":"relationshipId","label":"Resource x size matrix","type":"relationship","required":true,"help":"A matrix with a numbers scale"},{"name":"key","label":"Size of the mode","type":"expression","scope":"mode","required":true,"help":"e.g. tooling.size_class"},{"name":"demand","label":"Each takes","type":"expression","scope":"mode","default":"1"},{"name":"per","label":"Per","type":"select","options":["copy","item"],"default":"copy"}]', 60),
		('Configurations', 'Configurations', 'Resources', 'Combinations a resource can be set up in, e.g. 7 A tools, or 5 A and 1 B, or 4 A and 2 B. It''s in one combination at a time, changing between windows stops it for a while.',
			'a literal per combination per window, optional intervals take up what it doesn''t allow, a change blocks the resource', 1, 0, '[{"name":"relationshipId","label":"Combination x size matrix","type":"relationship","required":true,"help":"How many of each size each combination holds, a numbers scale"},{"name":"allowedRelationshipId","label":"Combinations each resource can use","type":"relationship","help":"A resource x combination list, blank lets every resource use every combination"},{"name":"key","label":"Size of the mode","type":"expression","scope":"mode","required":true,"help":"e.g. tooling.size_class"},{"name":"demand","label":"Each takes","type":"expression","scope":"mode","default":"1"},{"name":"per","label":"Per","type":"select","options":["copy","item"],"default":"copy"},{"name":"windowHours","label":"Can change every (hours)","type":"number","default":"24","help":"From midnight, 0 means it never changes in a plan"},{"name":"changeMinutes","label":"Minutes to change","type":"expression","scope":"resource","default":"0","help":"The resource runs nothing while it changes, e.g. 90 or resource.reset_minutes"},{"name":"changeCost","label":"Cost of each change","type":"number","default":"0"}]', 65),
		('Setup', 'Setup time', 'Time', 'Minutes to set a mode up at the start of each segment, e.g. fitting the tool or changing the platen configuration.',
			'added to each segment''s length', 1, 0, '[{"name":"minutes","label":"Minutes","type":"expression","scope":"mode","required":true,"help":"e.g. 45, or tooling.change_minutes"}]', 70),
		('Inspection', 'Inspection', 'Quality', 'Inspections that take time: after the first cycle, every N cycles, and on each setup or configuration change.',
			'added to each segment''s length, shown on the Gantt', 1, 0, '[{"name":"firstOffMinutes","label":"First off inspection minutes","type":"expression","scope":"item"},{"name":"everyCycles","label":"Every N cycles","type":"expression","scope":"item"},{"name":"everyMinutes","label":"Minutes each periodic inspection takes","type":"expression","scope":"item"},{"name":"setupMinutes","label":"Inspection minutes on each setup","type":"expression","scope":"item"}]', 80),
		('Split', 'Split', 'Sequence', 'An item may run in segments, each with its own resource and number of tools, e.g. 4 tools for a few cycles then 2 while another job takes the other 2.',
			'up to N optional segments per item', 1, 0, '[{"name":"maxSegments","label":"Most segments","type":"number","default":"2"},{"name":"minCycles","label":"Fewest cycles in a segment","type":"number","default":"1"},{"name":"allowParallel","label":"Segments may run at once","type":"condition","scope":"item","help":"true, or e.g. item.customer = ''ACME''"},{"name":"segmentPenalty","label":"Cost of each extra segment","type":"number","default":"50"}]', 90),
		('Compatible', 'Can share at once', 'Concurrency', 'A matrix of values, e.g. compounds or materials, that can or can''t run on the same resource at the same time.',
			'pairwise no overlap on a shared resource', 1, 0, '[{"name":"relationshipId","label":"Compatibility matrix","type":"relationship","required":true},{"name":"attribute","label":"Value of the item","type":"expression","scope":"item","required":true,"help":"e.g. item.compound"},{"name":"gapMinutes","label":"Gap between them","type":"number","default":"0"}]', 100),
		('MustMatch', 'Must match at once', 'Concurrency', 'Items on the same resource at the same time need the same value, e.g. cycle time or temperature, within a tolerance.',
			'pairwise no overlap on a shared resource', 1, 0, '[{"name":"attribute","label":"Value of the item","type":"expression","scope":"item","required":true,"help":"e.g. item.cycle_secs or item.compound.cure_temp_c"},{"name":"tolerance","label":"Tolerance","type":"number","default":"0"}]', 110),
		('Changeover', 'Changeover', 'Sequence', 'A gap between items whose values differ on the same resource, e.g. a colour change or an allergen clean down.',
			'pairwise separation with a gap', 1, 0, '[{"name":"attribute","label":"Value of the item","type":"expression","scope":"item","required":true},{"name":"minutes","label":"Minutes when they differ","type":"number","default":"30"},{"name":"relationshipId","label":"Minutes from a matrix","type":"relationship","help":"A value x value matrix of minutes, blank uses the minutes above"}]', 120),
		('Precedence', 'Precedence', 'Sequence', 'An item after another: from an attribute holding the other''s key, or in sequence within a group such as an order''s operations.',
			'linear precedence between segments', 1, 0, '[{"name":"predecessor","label":"Comes after","type":"expression","scope":"item","help":"e.g. item.parent_job"},{"name":"group","label":"Group","type":"expression","scope":"item","help":"e.g. item.order_no"},{"name":"sequence","label":"Sequence","type":"expression","scope":"item","help":"e.g. item.op_seq"},{"name":"gapMinutes","label":"Gap minutes","type":"number","default":"0"}]', 130),
		('SharedLimit', 'Shared limit', 'Resources', 'At most N of the chosen items at once across every resource, e.g. 2 silicone jobs, or the setters on shift.',
			'cumulative across resources', 1, 0, '[{"name":"capacity","label":"At most","type":"number","required":true,"default":"1"}]', 140),
		('Calendar', 'Calendars', 'Time', 'Resources only work in their calendar''s working time, a calendar takes its parent''s shifts and exceptions, e.g. the site''s shutdown.',
			'fixed blocked intervals', 1, 0, '[{"name":"applyTo","label":"Apply to","type":"select","options":["resource","tooling"],"default":"resource"}]', 150),
		('Goal', 'Goal', 'Goals', 'What a good plan is: on time (weighted by an attribute), short, compact, stable against the published plan, or planning everything.',
			'weighted objective terms', 0, 1, '[{"name":"goal","label":"Goal","type":"select","options":["Lateness","Makespan","Compact","Stability","Unscheduled","Preferences"],"required":true},{"name":"weightAttribute","label":"Weight each item by","type":"expression","scope":"item","help":"Lateness only, e.g. item.priority"},{"name":"higherIsMoreImportant","label":"Higher is more important","type":"boolean","default":"true"},{"name":"maxValue","label":"Highest value","type":"number","default":"10","help":"When lower is more important"},{"name":"timeWeight","label":"Weight per time bucket moved","type":"number","default":"1","help":"Stability only"}]', 160)
	) s (id, [name], category, [description], compilesTo, supportsHard, supportsSoft, [parameters], sortOrder) ON t.id = s.id
	WHEN MATCHED THEN UPDATE SET [name] = s.[name], category = s.category, [description] = s.[description], compilesTo = s.compilesTo,
		supportsHard = s.supportsHard, supportsSoft = s.supportsSoft, [parameters] = s.[parameters], sortOrder = s.sortOrder
	WHEN NOT MATCHED THEN INSERT (id, [name], category, [description], compilesTo, supportsHard, supportsSoft, [parameters], sortOrder)
		VALUES (s.id, s.[name], s.category, s.[description], s.compilesTo, s.supportsHard, s.supportsSoft, s.[parameters], s.sortOrder);
DELETE t
	FROM RuleTypes t
	WHERE t.id IN ('AssignTo', 'Consumes', 'Within', 'Outside', 'Before', 'SequenceGap', 'ConcurrentCompatibility', 'CountLimit', 'Same')
		AND NOT EXISTS (SELECT 1 FROM Rules r WHERE r.ruleTypeId = t.id)
GO

-- The built in value scales, they have no profile so every profile can use them
MERGE ValueScales t
	USING (VALUES
		('BOOLEAN', 'Boolean'),
		('GRADED', 'Graded'),
		('NUMBER', 'Number')
	) s (id, [name]) ON t.id = s.id
	WHEN MATCHED THEN UPDATE SET [name] = s.[name], profileId = NULL
	WHEN NOT MATCHED THEN INSERT (id, [name]) VALUES (s.id, s.[name]);
GO

MERGE ValueScaleValues t
	USING (VALUES
		('BOOLEAN', 'Y', 'Yes', 'Allowed', 0, '#2e7d32', 1),
		('BOOLEAN', 'N', 'No', 'Forbidden', 0, '#c62828', 2),
		('GRADED', 'P', 'Preferred', 'Penalty', 0, '#2e7d32', 1),
		('GRADED', 'A', 'Acceptable', 'Penalty', 10, '#9e9d24', 2),
		('GRADED', 'V', 'Avoid', 'Penalty', 100, '#ef6c00', 3),
		('GRADED', 'X', 'Not allowed', 'Forbidden', 0, '#c62828', 4),
		('NUMBER', '#', 'Number', 'Numeric', 0, NULL, 1)
	) s (valueScaleId, code, label, meaning, [weight], colour, sortOrder) ON t.valueScaleId = s.valueScaleId AND t.code = s.code
	WHEN MATCHED THEN UPDATE SET label = s.label, meaning = s.meaning, [weight] = s.[weight], colour = s.colour, sortOrder = s.sortOrder
	WHEN NOT MATCHED THEN INSERT (valueScaleId, code, label, meaning, [weight], colour, sortOrder)
		VALUES (s.valueScaleId, s.code, s.label, s.meaning, s.[weight], s.colour, s.sortOrder);
GO
