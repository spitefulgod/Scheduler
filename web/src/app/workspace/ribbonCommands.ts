// A ribbon or quick access button, milestone marks the ones that aren't built yet (99 for after the proof of concept)
export interface IRibbonButton {
    id : string;
    label : string;
    icon : string;
    size : 'large' | 'small';
    milestone? : number;
    // Shows as pressed while it's on, e.g. Compact
    toggle? : boolean;
}
export interface IRibbonGroup {
    label : string;
    buttons : IRibbonButton[];
}
export interface IRibbonTab {
    id : string;
    label : string;
    groups : IRibbonGroup[];
}

function large(id : string, label : string, icon : string, milestone? : number) : IRibbonButton {
    return { id : id, label : label, icon : icon, size : 'large', milestone : milestone };
}
function small(id : string, label : string, icon : string, milestone? : number) : IRibbonButton {
    return { id : id, label : label, icon : icon, size : 'small', milestone : milestone };
}
function toggle(button : IRibbonButton) : IRibbonButton {
    return { ...button, toggle : true };
}

// The ribbon's tabs, groups and buttons, CommandService runs them
export const RIBBON : IRibbonTab[] = [
    { id : 'home', label : 'Home', groups : [
        { label : 'Profile', buttons : [large('profile.new', 'New Profile', 'note_add'), large('profile.open', 'Open', 'folder_open'),
            small('profile.save', 'Save', 'save'), small('profile.properties', 'Properties', 'tune'), small('profile.duplicate', 'Duplicate', 'content_copy', 99)] },
        { label : 'Edit', buttons : [small('edit.undo', 'Undo', 'undo', 99), small('edit.redo', 'Redo', 'redo', 99)] },
        { label : 'Check', buttons : [large('profile.validate', 'Validate', 'fact_check')] },
        { label : 'Security', buttons : [large('security.users', 'Users & Roles', 'admin_panel_settings')] }
    ] },
    { id : 'data', label : 'Data', groups : [
        { label : 'Connections', buttons : [large('connection.new', 'New Connection', 'add_link'),
            small('connection.test', 'Test', 'network_check'), small('connection.edit', 'Edit', 'edit'), small('connection.refresh', 'Refresh Schema', 'sync')] },
        { label : 'Sources', buttons : [large('source.add', 'Add Source', 'add_box'), small('source.preview', 'Preview', 'preview'),
            small('source.refresh', 'Refresh Columns', 'view_column'), small('source.map', 'Map Columns', 'schema')] }
    ] },
    { id : 'model', label : 'Model', groups : [
        { label : 'Entities', buttons : [large('entity.new', 'New Entity', 'category'), small('entity.attributes', 'Attributes', 'data_object'),
            small('entity.roles', 'Roles', 'badge')] },
        { label : 'Duration', buttons : [large('entity.duration', 'Duration Formula', 'function')] }
    ] },
    { id : 'setup', label : 'Setup', groups : [
        { label : 'Relationships', buttons : [large('relationship.matrix', 'New Matrix', 'grid_on'), large('relationship.list', 'New List', 'list'),
            small('relationship.scales', 'Value Scales', 'linear_scale')] },
        { label : 'Import', buttons : [large('relationship.import', 'Import from Source', 'download')] },
        { label : 'Time', buttons : [large('calendar.edit', 'Calendars', 'calendar_month')] }
    ] },
    { id : 'rules', label : 'Rules', groups : [
        { label : 'Rules', buttons : [large('rule.new', 'New Rule', 'rule'), small('rule.duplicate', 'Duplicate', 'content_copy'),
            small('rule.toggle', 'Enable/Disable', 'toggle_on')] },
        { label : 'Check', buttons : [large('rule.conflicts', 'Check Conflicts', 'report')] }
    ] },
    { id : 'schedule', label : 'Schedule', groups : [
        { label : 'Solve', buttons : [large('solve.run', 'Solve', 'play_arrow'), small('solve.stop', 'Stop', 'stop'),
            small('solve.settings', 'Settings', 'settings')] },
        { label : 'Views', buttons : [large('solve.gantt', 'Gantt', 'view_timeline'), large('solve.table', 'Table', 'table_rows')] },
        { label : 'Plan', buttons : [large('solve.export', 'Publish', 'upload'), small('solve.emergency', 'Resource Down', 'emergency'), small('solve.locks', 'Locks', 'lock')] }
    ] },
    { id : 'view', label : 'View', groups : [
        { label : 'Panels', buttons : [toggle(large('view.navigator', 'Navigator', 'side_navigation')),
            toggle(large('view.properties', 'Properties', 'right_panel_open'))] },
        { label : 'Appearance', buttons : [toggle(small('view.density', 'Compact', 'density_small')),
            toggle(small('view.sidebar', 'Collapse sidebar', 'left_panel_close')), toggle(small('view.propertiesCollapsed', 'Collapse properties', 'right_panel_close'))] }
    ] }
];

// The tabs a solver sees, the rest are the model behind the schedule
export const SOLVER_TABS : string[] = ['home', 'schedule', 'view'];

// The buttons at the top left, beside the title
export const QUICK_ACCESS : IRibbonButton[] = [
    small('profile.save', 'Save', 'save'),
    small('edit.undo', 'Undo', 'undo', 99),
    small('edit.redo', 'Redo', 'redo', 99),
    small('solve.run', 'Solve', 'play_arrow')
];
