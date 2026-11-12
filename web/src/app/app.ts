import { Component, inject, OnInit } from '@angular/core';
import { DocHost } from './shell/docHost/docHost';
import { Navigator } from './shell/navigator/navigator';
import { Properties } from './shell/properties/properties';
import { Ribbon } from './shell/ribbon/ribbon';
import { StatusBar } from './shell/statusBar/statusBar';
import { WorkspaceService } from './workspace/workspaceService';

// The whole screen, ribbon at the top, navigator, documents and properties across the middle, status bar at the bottom
@Component({
    selector : 'app-root',
    imports : [Ribbon, Navigator, DocHost, Properties, StatusBar],
    templateUrl : './app.html',
    styleUrl : './app.scss',
})
export class App implements OnInit {
    readonly workspace = inject(WorkspaceService);

    ngOnInit() : void {
        this.workspace.init();
    }
    getToastIcon(tone : string) : string {
        return tone == 'ok' ? 'check_circle' : tone == 'error' ? 'error' : 'info';
    }
}
