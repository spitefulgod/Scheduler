import { Component, inject } from '@angular/core';
import { CommandService } from '../../workspace/commandService';
import { WorkspaceService } from '../../workspace/workspaceService';

// The start tab, ways in, what's in the open profile and how far the milestones have got
@Component({
    selector : 'app-welcome-page',
    templateUrl : './welcomePage.html',
    styleUrl : './welcomePage.scss',
})
export class WelcomePage {
    readonly workspace = inject(WorkspaceService);
    readonly commands = inject(CommandService);
    readonly milestones = [
        { number : 1, done : true, text : 'Scaffold, SchedulerMeta database, ribbon shell' },
        { number : 2, done : true, text : 'Connections, source browser, read only preview' },
        { number : 3, done : true, text : 'Entities, attributes, roles, column mapping' },
        { number : 4, done : true, text : 'Value scales, matrix and list editors, calendars' },
        { number : 5, done : true, text : 'Rule blocks and rule builder' },
        { number : 6, done : true, text : 'CP-SAT solver, Gantt, infeasibility explanation' },
        { number : 7, done : false, text : 'Second profile through configuration only' }
    ];
}
