import { Component, computed, inject, OnInit } from '@angular/core';
import { DocHost } from './shell/docHost/docHost';
import { Navigator } from './shell/navigator/navigator';
import { Properties } from './shell/properties/properties';
import { Ribbon } from './shell/ribbon/ribbon';
import { SideBar } from './shell/sideBar/sideBar';
import { StatusBar } from './shell/statusBar/statusBar';
import { WorkspaceService } from './workspace/workspaceService';

// The whole screen, the navigation sidebar down the left, then the ribbon, the navigator, documents and properties, and the
// status bar at the bottom. Until the Windows login has worked it's the signing in message, or the login error page
@Component({
    selector : 'app-root',
    imports : [SideBar, Ribbon, Navigator, DocHost, Properties, StatusBar],
    templateUrl : './app.html',
    styleUrl : './app.scss',
})
export class App implements OnInit {
    readonly workspace = inject(WorkspaceService);
    // the navigator holds the model, a solver only has the runs in it so it's left out
    readonly showNavigator = computed<boolean>(() => this.workspace.showNavigator() && !this.workspace.solverOnly());

    ngOnInit() : void {
        this.workspace.init();
    }
    // Runs the login again, the app takes over when it works
    retry() : void {
        this.workspace.init();
    }
    getToastIcon(tone : string) : string {
        return tone == 'ok' ? 'check_circle' : tone == 'error' ? 'error' : 'info';
    }
}
