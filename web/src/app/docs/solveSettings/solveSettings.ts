import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IProfileSettings, SchedulerApi } from '../../api/schedulerApi';
import { WorkspaceService } from '../../workspace/workspaceService';

// The profile's run settings: how far ahead to plan, how long to search, what's locked and how the Gantt shows it
@Component({
    selector : 'app-solve-settings',
    imports : [FormsModule],
    templateUrl : './solveSettings.html',
    styleUrl : './solveSettings.scss',
})
export class SolveSettings implements OnInit {
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);
    readonly busy = signal<boolean>(false);
    readonly attributes = signal<string[]>([]);
    settings : IProfileSettings = {};

    async ngOnInit() : Promise<void> {
        const profileId = this.workspace.profileId();
        this.settings = { horizonDays : 28, timeLimitSeconds : 30, allowUnscheduled : true, lockHours : 0, colourBy : null, labelBy : null, ...(this.workspace.profile()?.settings ?? {}) };
        if(!profileId)
            return;
        try {
            const entities = await this.api.getEntities(profileId);
            this.attributes.set(entities.find(x => x.kind == 'Item')?.attributes.map(x => x.name) ?? []);
        }
        catch(e) {
        }
    }
    async save() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.busy.set(true);
        try {
            await this.api.saveProfileSettings(profileId, this.settings);
            await this.workspace.refreshProfiles();
            this.workspace.toast('Settings saved', 'ok');
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
}
