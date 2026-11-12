import { Component, inject } from '@angular/core';
import { WorkspaceService } from '../../workspace/workspaceService';

// The bar along the bottom, the database in use, how many problems there are and the last solve
@Component({
    selector : 'app-status-bar',
    templateUrl : './statusBar.html',
    styleUrl : './statusBar.scss',
})
export class StatusBar {
    readonly workspace = inject(WorkspaceService);
}
