import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IScaleValue, IValueScale, SchedulerApi } from '../../api/schedulerApi';
import { WorkspaceService } from '../../workspace/workspaceService';

// The value scales matrices use, each value says what it means to the solver: forbidden, allowed, a penalty or a number,
// the built in ones are shared and can't be changed, a profile can add its own
@Component({
    selector : 'app-value-scales',
    imports : [FormsModule],
    templateUrl : './valueScales.html',
    styleUrl : './valueScales.scss',
})
export class ValueScales implements OnInit {
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);
    readonly scales = signal<IValueScale[]>([]);
    readonly editing = signal<IValueScale | null>(null);
    readonly error = signal<string>('');
    readonly meanings = ['Forbidden', 'Allowed', 'Penalty', 'Numeric'];

    async ngOnInit() : Promise<void> {
        await this.load();
    }
    async load() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        try {
            this.scales.set(await this.api.getValueScales(profileId));
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
    }
    edit(scale : IValueScale | null) : void {
        this.editing.set(scale
            ? { ...scale, values : scale.values.map(x => ({ ...x })) }
            : { id : '', profileId : this.workspace.profileId(), name : '', values : [{ code : 'Y', label : 'Yes', meaning : 'Allowed', weight : 0, colour : '#2e7d32' }] });
    }
    addValue(scale : IValueScale) : void {
        scale.values.push({ code : '', label : '', meaning : 'Penalty', weight : 10, colour : '#9e9d24' } as IScaleValue);
    }
    removeValue(scale : IValueScale, index : number) : void {
        scale.values.splice(index, 1);
    }
    async save() : Promise<void> {
        const profileId = this.workspace.profileId();
        const scale = this.editing();
        if(!profileId || !scale)
            return;
        try {
            await this.api.saveValueScale(profileId, scale.id || null, scale.name, scale.values);
            this.workspace.toast('Scale saved', 'ok');
            this.editing.set(null);
            await this.load();
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }
}
