import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { getErrorMessage, IPreview, ISource, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';
import { DataGrid } from '../dataGrid/dataGrid';

// A source in the profile, its columns as last read and a read only preview of its rows
@Component({
    selector : 'app-source-doc',
    imports : [DataGrid],
    templateUrl : './sourceDoc.html',
    styleUrl : './sourceDoc.scss',
})
export class SourceDoc {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);

    readonly source = signal<ISource | null>(null);
    readonly preview = signal<IPreview | null>(null);
    readonly error = signal<string>('');
    readonly previewError = signal<string>('');
    readonly sourceId = computed<string>(() => this.doc().params['sourceId'] as string);

    constructor() {
        // loads again when the document is opened with a new reload
        effect(() => {
            this.doc().params['reload'];
            const sourceId = this.sourceId();
            untracked(() => this.load(sourceId));
        });
    }
    getDescription(source : ISource) : string {
        const what = source.kind == 'query' ? 'Saved select' : `${source.schema}.${source.objectName}`;
        const read = source.columnsRead ? source.columnsRead.substring(0, 16).replace('T', ' ') : 'never';
        return `${what}, ${source.columns.length} columns, read ${read}`;
    }
    async loadPreview() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.previewError.set('');
        this.preview.set(null);
        try {
            this.preview.set(await this.api.getSourcePreview(profileId, this.sourceId()));
        }
        catch(e) {
            this.previewError.set(getErrorMessage(e));
        }
    }
    async refreshColumns() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        try {
            this.source.set(await this.api.refreshSource(profileId, this.sourceId()));
            await this.workspace.refreshNavigator();
            this.workspace.toast('Columns read again', 'ok');
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }

    private async load(sourceId : string) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        try {
            this.source.set(await this.api.getSource(profileId, sourceId));
            await this.loadPreview();
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
    }
}
