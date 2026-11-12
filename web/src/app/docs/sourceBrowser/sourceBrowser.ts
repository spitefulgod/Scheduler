import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IColumn, IPreview, ISourceObject, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';
import { DataGrid } from '../dataGrid/dataGrid';

// The tables and views of one schema
interface ISchemaGroup {
    schema : string;
    objects : ISourceObject[];
}

// A connection's tables and views by schema with a search, their columns and a preview of the first 200 rows
@Component({
    selector : 'app-source-browser',
    imports : [FormsModule, DataGrid],
    templateUrl : './sourceBrowser.html',
    styleUrl : './sourceBrowser.scss',
})
export class SourceBrowser {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    readonly workspace = inject(WorkspaceService);

    readonly objects = signal<ISourceObject[]>([]);
    readonly loading = signal<boolean>(false);
    readonly error = signal<string>('');
    readonly search = signal<string>('');
    readonly selected = signal<ISourceObject | null>(null);
    readonly columns = signal<IColumn[]>([]);
    readonly preview = signal<IPreview | null>(null);
    readonly previewLoading = signal<boolean>(false);
    readonly view = signal<'preview' | 'columns'>('preview');
    readonly connectionId = computed<string>(() => this.doc().params['connectionId'] as string);
    sourceName : string = '';

    // The columns list shown in the grid
    readonly columnGridColumns : IColumn[] = [
        { name : 'Column', sqlType : '', nullable : false },
        { name : 'Type', sqlType : '', nullable : false },
        { name : 'Nullable', sqlType : '', nullable : false }
    ];
    readonly columnGridRows = computed<unknown[][]>(() => this.columns().map(x => [x.name, x.sqlType, x.nullable ? 'yes' : 'no']));

    readonly groups = computed<ISchemaGroup[]>(() => {
        const search = this.search().trim().toLowerCase();
        const groups : ISchemaGroup[] = [];
        for(const item of this.objects()) {
            if(search && !`${item.schema}.${item.name}`.toLowerCase().includes(search))
                continue;
            let group = groups.find(x => x.schema == item.schema);
            if(!group) {
                group = { schema : item.schema, objects : [] };
                groups.push(group);
            }
            group.objects.push(item);
        }
        return groups;
    });
    // What's already a source in the profile, so it can be marked
    readonly existingSources = computed<string[]>(() => this.workspace.getFolder('sources').map(x => String(x.properties?.['Object'] ?? '').toLowerCase()));

    constructor() {
        // reads the connection again when the document is opened with a new reload
        effect(() => {
            this.doc().params['reload'];
            const connectionId = this.connectionId();
            untracked(() => this.load(connectionId));
        });
    }
    async load(connectionId : string) : Promise<void> {
        this.loading.set(true);
        this.error.set('');
        try {
            this.objects.set(await this.api.getObjects(connectionId));
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
        this.loading.set(false);
    }
    async pick(item : ISourceObject) : Promise<void> {
        this.selected.set(item);
        this.sourceName = item.name;
        this.preview.set(null);
        this.columns.set([]);
        this.previewLoading.set(true);
        try {
            const [columns, preview] = await Promise.all([
                this.api.getObjectColumns(this.connectionId(), item.schema, item.name),
                this.api.getObjectPreview(this.connectionId(), item.schema, item.name)
            ]);
            this.columns.set(columns);
            this.preview.set(preview);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.previewLoading.set(false);
    }
    // e.g. 8 columns, 150 rows shown
    getSummary() : string {
        const preview = this.preview();
        const rows = preview ? `, ${preview.rows.length}${preview.truncated ? '+' : ''} rows shown` : '';
        return `${this.columns().length} columns${rows}`;
    }
    isSelected(item : ISourceObject) : boolean {
        return this.selected()?.schema == item.schema && this.selected()?.name == item.name;
    }
    isAdded(item : ISourceObject) : boolean {
        return this.existingSources().includes(`${item.schema}.${item.name}`.toLowerCase());
    }
    onSearch(event : Event) : void {
        this.search.set((event.target as HTMLInputElement).value);
    }
    async addSource() : Promise<void> {
        const item = this.selected();
        const profileId = this.workspace.profileId();
        if(!item || !profileId)
            return;
        try {
            const source = await this.api.saveSource(profileId, null, {
                connectionId : this.connectionId(),
                name : this.sourceName.trim() || item.name,
                kind : item.type,
                schema : item.schema,
                objectName : item.name,
                queryText : null
            });
            await this.workspace.refreshNavigator();
            this.workspace.toast(`Added source ${source.name} with ${source.columns.length} columns`, 'ok');
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }
}
