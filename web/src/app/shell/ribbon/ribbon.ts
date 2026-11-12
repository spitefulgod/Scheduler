import { Component, computed, inject, signal } from '@angular/core';
import { CommandService } from '../../workspace/commandService';
import { IRibbonButton, IRibbonGroup, IRibbonTab, QUICK_ACCESS, RIBBON, SOLVER_TABS } from '../../workspace/ribbonCommands';
import { WorkspaceService } from '../../workspace/workspaceService';

// The Office style ribbon, a title bar with the quick access buttons, the tabs, then groups of large and stacked small buttons
@Component({
    selector : 'app-ribbon',
    templateUrl : './ribbon.html',
    styleUrl : './ribbon.scss',
})
export class Ribbon {
    readonly workspace = inject(WorkspaceService);
    readonly commands = inject(CommandService);
    // a solver only gets the tabs for the schedule
    readonly tabs = computed<IRibbonTab[]>(() => this.workspace.solverOnly() ? RIBBON.filter(x => SOLVER_TABS.includes(x.id)) : RIBBON);
    readonly quickAccess = QUICK_ACCESS;
    readonly activeTab = signal<string>('home');
    readonly tab = computed<IRibbonTab>(() => this.tabs().find(x => x.id == this.activeTab()) ?? this.tabs()[0]);

    getLarge(group : IRibbonGroup) : IRibbonButton[] {
        return group.buttons.filter(x => x.size == 'large');
    }
    // Small buttons stack three to a column
    getSmallColumns(group : IRibbonGroup) : IRibbonButton[][] {
        const small = group.buttons.filter(x => x.size == 'small');
        const columns : IRibbonButton[][] = [];
        for(let i = 0; i < small.length; i += 3)
            columns.push(small.slice(i, i + 3));
        return columns;
    }
    selectTab(tabId : string) : void {
        if(this.workspace.ribbonCollapsed() && this.activeTab() == tabId)
            return;
        this.activeTab.set(tabId);
    }
    toggleCollapsed() : void {
        this.workspace.ribbonCollapsed.update(x => !x);
    }
}
