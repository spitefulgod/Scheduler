import { inject, Injectable } from '@angular/core';
import { getErrorMessage, SchedulerApi } from '../api/schedulerApi';
import { IRibbonButton } from './ribbonCommands';
import { WorkspaceService } from './workspaceService';

// Runs the ribbon and quick access buttons
@Injectable({ providedIn : 'root' })
export class CommandService {
    private readonly workspace = inject(WorkspaceService);
    private readonly api = inject(SchedulerApi);

    isEnabled(button : IRibbonButton) : boolean {
        if(button.milestone)
            return false;
        if(!this.isAllowed(button.id))
            return false;
        switch(button.id) {
            // changes are saved as they're made, until documents know when they've been changed
            case 'profile.save':
                return false;
            case 'profile.properties':
            case 'source.add':
                return this.workspace.profile() != null;
            case 'connection.test':
            case 'connection.refresh':
                return this.workspace.selectedConnectionId() != null;
            case 'connection.edit':
                return this.workspace.selectedConnectionId() != null && !this.isDemoConnection(this.workspace.selectedConnectionId());
            case 'source.preview':
            case 'source.refresh':
                return this.workspace.selectedSourceId() != null;
            case 'rule.duplicate':
            case 'rule.toggle':
                return this.workspace.selection()?.kind == 'rule';
            case 'relationship.import':
                return this.workspace.selection()?.kind == 'relationship' || this.workspace.activeDoc()?.kind == 'relationship';
            case 'solve.run':
                return this.workspace.profile() != null && !this.workspace.solving();
            case 'solve.stop':
                return this.workspace.solving();
            case 'solve.export':
                return this.workspace.currentRunId() != null && !this.workspace.solving();
            default:
                return true;
        }
    }
    // What the user's roles let them use, the API refuses the rest anyway
    isAllowed(commandId : string) : boolean {
        const workspace = this.workspace;
        const rights = workspace.rights();
        const area = commandId.split('.')[0];
        switch(commandId) {
            case 'profile.new':
                return workspace.user()?.siteOwner == true;
            case 'profile.open':
                return true;
            case 'profile.properties':
            case 'profile.validate':
            case 'rule.conflicts':
                return rights.readModel;
            case 'security.users':
                return workspace.canManageUsers();
            case 'solve.gantt':
            case 'solve.table':
                return rights.viewSchedule;
        }
        switch(area) {
            case 'connection':
                return workspace.user()?.designer == true;
            case 'source':
            case 'entity':
            case 'relationship':
            case 'calendar':
            case 'rule':
                return rights.editModel;
            case 'solve':
                return rights.solve;
            default:
                return true;
        }
    }
    isActive(button : IRibbonButton) : boolean {
        switch(button.id) {
            case 'view.navigator':
                return this.workspace.showNavigator();
            case 'view.properties':
                return this.workspace.showProperties();
            case 'view.density':
                return this.workspace.density() == 'compact';
            case 'view.sidebar':
                return this.workspace.sidebarCollapsed();
            case 'view.propertiesCollapsed':
                return this.workspace.propertiesCollapsed();
            default:
                return false;
        }
    }
    getTooltip(button : IRibbonButton) : string {
        if(button.milestone)
            return button.milestone > 7 ? `${button.label}, not in the proof of concept yet` : `${button.label}, coming in milestone ${button.milestone}`;
        if(button.id == 'profile.save')
            return 'Changes are saved as they are made';
        if(!button.milestone && !this.isAllowed(button.id))
            return `${button.label}, your role doesn't allow it`;
        return button.label;
    }

    async run(commandId : string) : Promise<void> {
        const workspace = this.workspace;
        try {
            switch(commandId) {
                case 'profile.new':
                    workspace.openDoc({ id : 'profile:new', kind : 'profile', title : 'New profile', icon : 'note_add', params : {} });
                    break;
                case 'profile.open':
                    workspace.openDoc({ id : 'openProfile', kind : 'openProfile', title : 'Open profile', icon : 'folder_open', params : {} });
                    break;
                case 'profile.properties': {
                    const profile = workspace.profile();
                    if(profile)
                        workspace.openDoc({ id : `profile:${profile.id}`, kind : 'profile', title : profile.name, icon : 'tune', params : { profileId : profile.id } });
                    break;
                }
                case 'connection.new':
                    workspace.openDoc({ id : 'connection:new', kind : 'connection', title : 'New connection', icon : 'add_link', params : {} });
                    break;
                case 'connection.edit': {
                    const connectionId = workspace.selectedConnectionId();
                    if(connectionId)
                        workspace.openDoc({ id : `connection:${connectionId}`, kind : 'connection', title : 'Edit connection', icon : 'edit', params : { connectionId : connectionId } });
                    break;
                }
                case 'connection.test': {
                    const connectionId = workspace.selectedConnectionId();
                    if(!connectionId)
                        break;
                    const tested = await this.api.testConnection(connectionId);
                    if(tested.lastTestOk)
                        workspace.toast('Connection test passed', 'ok');
                    else
                        workspace.toast(`Connection test failed: ${tested.lastTestMessage}`, 'error');
                    await workspace.refreshNavigator();
                    break;
                }
                // both open the source browser, on the selected connection or the first one
                case 'connection.refresh':
                case 'source.add': {
                    const connections = workspace.getFolder('connections');
                    const connectionId = workspace.selectedConnectionId() ?? connections[0]?.itemId ?? null;
                    if(!connectionId) {
                        workspace.toast('Add a connection first', 'info');
                        break;
                    }
                    workspace.openSourceBrowser(connectionId, connections.find(x => x.itemId == connectionId)?.label ?? 'Source browser', true);
                    break;
                }
                case 'source.preview': {
                    const sourceId = workspace.selectedSourceId();
                    const selected = workspace.selection();
                    if(sourceId)
                        workspace.openDoc({ id : `source:${sourceId}`, kind : 'source', title : selected?.kind == 'source' ? selected.label : 'Source', icon : 'table', params : { sourceId : sourceId } });
                    break;
                }
                case 'source.refresh': {
                    const sourceId = workspace.selectedSourceId();
                    const profileId = workspace.profileId();
                    if(!sourceId || !profileId)
                        break;
                    const source = await this.api.refreshSource(profileId, sourceId);
                    workspace.toast(`Read ${source.columns.length} columns for ${source.name}`, 'ok');
                    await workspace.refreshNavigator();
                    workspace.openDoc({ id : `source:${sourceId}`, kind : 'source', title : source.name, icon : 'table', params : { sourceId : sourceId, reload : Date.now() } });
                    break;
                }
                case 'entity.new':
                    workspace.openDoc({ id : 'entity:new', kind : 'entity', title : 'New entity', icon : 'category', params : {} });
                    break;
                case 'source.map':
                case 'entity.attributes':
                case 'entity.roles':
                case 'entity.duration':
                    this.openSelected('entity', 'entities');
                    break;
                case 'relationship.matrix':
                case 'relationship.list': {
                    const editor = commandId == 'relationship.matrix' ? 'Matrix' : 'List';
                    workspace.openDoc({ id : 'relationship:new', kind : 'relationship', title : `New ${editor.toLowerCase()}`, icon : 'grid_on', params : { editor : editor } });
                    break;
                }
                case 'relationship.scales':
                    workspace.openDoc({ id : 'scales', kind : 'scales', title : 'Value scales', icon : 'linear_scale', params : {} });
                    break;
                case 'relationship.import': {
                    const profileId = workspace.profileId();
                    const selected = workspace.selection();
                    const relationshipId = selected?.kind == 'relationship' ? selected.itemId : workspace.activeDoc()?.params['relationshipId'] as string | undefined;
                    if(!profileId || !relationshipId)
                        break;
                    await this.api.importValues(profileId, relationshipId);
                    workspace.toast('Copied the source values in as typed in values', 'ok');
                    await workspace.refreshNavigator();
                    workspace.openDoc({ id : `relationship:${relationshipId}`, kind : 'relationship', title : 'Relationship', icon : 'grid_on', params : { relationshipId : relationshipId, reload : Date.now() } });
                    break;
                }
                case 'calendar.edit':
                    if(!this.openSelected('calendar', null))
                        workspace.openDoc({ id : 'calendar:new', kind : 'calendar', title : 'New calendar', icon : 'calendar_month', params : {} });
                    break;
                case 'rule.new':
                    workspace.openDoc({ id : 'rule:new', kind : 'rule', title : 'New rule', icon : 'rule', params : {} });
                    break;
                case 'rule.duplicate':
                case 'rule.toggle': {
                    const profileId = workspace.profileId();
                    const ruleId = workspace.selection()?.itemId;
                    if(!profileId || !ruleId)
                        break;
                    const rule = (await this.api.getRules(profileId)).find(x => x.id == ruleId);
                    if(!rule)
                        break;
                    const details = { name : rule.name, ruleTypeId : rule.ruleTypeId, enabled : rule.enabled, strength : rule.strength, weight : rule.weight, blocks : rule.blocks, sortOrder : rule.sortOrder };
                    if(commandId == 'rule.toggle') {
                        await this.api.saveRule(profileId, rule.id, { ...details, enabled : !rule.enabled });
                        workspace.toast(`${rule.name} is ${rule.enabled ? 'off' : 'on'}`, 'ok');
                    }
                    else {
                        const copy = await this.api.saveRule(profileId, null, { ...details, name : `${rule.name} (copy)`, sortOrder : rule.sortOrder + 1 });
                        workspace.openDoc({ id : `rule:${copy.id}`, kind : 'rule', title : copy.name, icon : 'rule', params : { ruleId : copy.id } });
                    }
                    await workspace.refreshNavigator();
                    break;
                }
                case 'profile.validate':
                case 'rule.conflicts': {
                    const profileId = workspace.profileId();
                    if(!profileId)
                        break;
                    const check = await this.api.checkRules(profileId);
                    const errors = check.diagnostics.filter(x => x.level == 'error').length;
                    const warnings = check.diagnostics.length - errors;
                    workspace.toast(`${check.items} items, ${check.resources} resources, ${check.modes} ways to run them, ${check.noMode} with none. ${errors} errors, ${warnings} warnings.`
                        + (check.diagnostics[0] ? ` First: ${check.diagnostics[0].message}` : ''), errors ? 'error' : 'ok');
                    break;
                }
                case 'solve.run': {
                    const profileId = workspace.profileId();
                    if(!profileId)
                        break;
                    const run = await this.api.solve(profileId, { kind : 'Solve' });
                    workspace.openRun(run.id);
                    await workspace.refreshNavigator();
                    break;
                }
                case 'solve.stop': {
                    const profileId = workspace.profileId();
                    const runId = workspace.currentRunId();
                    if(profileId && runId)
                        await this.api.stopRun(profileId, runId);
                    break;
                }
                case 'solve.export': {
                    const profileId = workspace.profileId();
                    const runId = workspace.currentRunId();
                    if(!profileId || !runId)
                        break;
                    await this.api.publishRun(profileId, runId);
                    workspace.toast('Published, this is now the plan', 'ok');
                    await workspace.refreshNavigator();
                    workspace.openRun(runId);
                    break;
                }
                case 'solve.settings':
                    workspace.openDoc({ id : 'solveSettings', kind : 'solveSettings', title : 'Solve settings', icon : 'settings', params : {} });
                    break;
                case 'solve.gantt':
                case 'solve.table':
                case 'solve.emergency':
                case 'solve.locks':
                    workspace.scheduleView.set(commandId == 'solve.table' ? 'table' : 'gantt');
                    if(workspace.activeDoc()?.kind != 'schedule')
                        workspace.openRun(workspace.currentRunId());
                    break;
                case 'view.navigator':
                    workspace.showNavigator.update(x => !x);
                    break;
                case 'view.properties':
                    workspace.showProperties.update(x => !x);
                    break;
                case 'view.density':
                    workspace.density.update(x => x == 'compact' ? 'comfortable' : 'compact');
                    break;
                case 'view.sidebar':
                    workspace.toggleSidebar();
                    break;
                case 'view.propertiesCollapsed':
                    if(!workspace.showProperties())
                        workspace.showProperties.set(true);
                    workspace.toggleProperties();
                    break;
                case 'security.users':
                    workspace.openDoc({ id : 'users', kind : 'users', title : 'Users & roles', icon : 'admin_panel_settings', params : {} });
                    break;
            }
        }
        catch(e) {
            workspace.toast(getErrorMessage(e), 'error');
        }
    }
    // Opens the selected item of a kind, or the first in its folder, false when there's none
    private openSelected(kind : string, folderId : string | null) : boolean {
        const selected = this.workspace.selection();
        const node = selected?.kind == kind ? selected : folderId ? this.workspace.getFolder(folderId)[0] : undefined;
        if(!node)
            return false;
        this.workspace.openNode(node);
        return true;
    }
    // The demo connection is built in, it can't be changed
    isDemoConnection(connectionId : string | null) : boolean {
        const node = this.workspace.getFolder('connections').find(x => x.itemId == connectionId);
        return node?.properties?.['Server'] == '(SchedulerMeta)';
    }
}
