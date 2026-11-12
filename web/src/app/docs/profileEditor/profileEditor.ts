import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';

// Adds a profile, or changes the name, description and time unit of one
@Component({
    selector : 'app-profile-editor',
    imports : [FormsModule],
    templateUrl : './profileEditor.html',
    styleUrl : './profileEditor.scss',
})
export class ProfileEditor implements OnInit {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);
    readonly busy = signal<boolean>(false);
    readonly error = signal<string>('');
    readonly isNew = signal<boolean>(true);
    name : string = '';
    description : string = '';
    timeUnitMinutes : number = 15;

    ngOnInit() : void {
        const profile = this.workspace.profiles().find(x => x.id == this.doc().params['profileId']);
        if(!profile)
            return;
        this.isNew.set(false);
        this.name = profile.name;
        this.description = profile.description ?? '';
        this.timeUnitMinutes = profile.timeUnitMinutes;
    }
    async save() : Promise<void> {
        this.busy.set(true);
        this.error.set('');
        try {
            const profileId = (this.doc().params['profileId'] as string | undefined) ?? null;
            const saved = await this.api.saveProfile(profileId, this.name, this.description || null, this.timeUnitMinutes);
            await this.workspace.refreshProfiles();
            this.workspace.closeDoc(this.doc().id);
            if(!profileId)
                await this.workspace.openProfile(saved.id);
            this.workspace.toast(`Profile ${saved.name} saved`, 'ok');
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
        this.busy.set(false);
    }
}
