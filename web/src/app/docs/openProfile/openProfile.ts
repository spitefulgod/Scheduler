import { Component, inject, input, OnInit } from '@angular/core';
import { getErrorMessage } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';

// Picks the profile to work on
@Component({
    selector : 'app-open-profile',
    templateUrl : './openProfile.html',
    styleUrl : './openProfile.scss',
})
export class OpenProfile implements OnInit {
    readonly doc = input.required<IDocTab>();
    readonly workspace = inject(WorkspaceService);

    ngOnInit() : void {
        this.workspace.refreshProfiles().catch(e => this.workspace.toast(getErrorMessage(e), 'error'));
    }
    async open(profileId : string) : Promise<void> {
        await this.workspace.openProfile(profileId);
        this.workspace.closeDoc(this.doc().id);
    }
}
