import { Component, computed, inject } from '@angular/core';
import { CommandService } from '../../workspace/commandService';
import { WorkspaceService } from '../../workspace/workspaceService';

// The dark navigation sidebar down the left, from the Recommit upload site: the brand mark (which shrinks it to its icons),
// the profiles the user has a role in, the site's pages, and who is signed in at the bottom
@Component({
    selector : 'app-side-bar',
    templateUrl : './sideBar.html',
    styleUrl : './sideBar.scss',
})
export class SideBar {
    readonly workspace = inject(WorkspaceService);
    readonly commands = inject(CommandService);

    // What the user is in the open profile, shown under their name
    readonly roleLabel = computed<string>(() => {
        const user = this.workspace.user();
        const rights = this.workspace.rights();
        if(user?.siteOwner)
            return 'Site owner';
        if(!this.workspace.profileId())
            return 'No profiles yet';
        if(rights.manageUsers)
            return 'Project owner';
        if(rights.readModel && rights.solve)
            return 'Project reader and solver';
        if(rights.readModel)
            return 'Project reader';
        if(rights.solve)
            return 'Project solver';
        return 'No role on this profile';
    });

    async openProfile(profileId : string) : Promise<void> {
        if(profileId != this.workspace.profileId())
            await this.workspace.openProfile(profileId);
    }
    run(commandId : string) : void {
        this.commands.run(commandId);
    }
}
