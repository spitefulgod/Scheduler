import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, EntityKind, IAttribute, IColumn, IEntity, IEntityRows, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';
import { DataGrid } from '../dataGrid/dataGrid';

const ROLES = ['Id', 'Name', 'Duration', 'Quantity', 'CycleTime', 'Capacity', 'Copies', 'Size', 'Category', 'ReleaseDate', 'DueDate', 'Priority', 'ParentItem', 'Sequence', 'Custom'];
const TYPES = ['STRING', 'INTEGER', 'DECIMAL', 'BOOLEAN', 'DATETIME'];

// An entity, e.g. Job or Press: what kind it is, the source it reads, and each attribute mapped to a column with its role,
// with the rows as the rule engine will see them
@Component({
    selector : 'app-entity-editor',
    imports : [FormsModule, DataGrid],
    templateUrl : './entityEditor.html',
    styleUrl : './entityEditor.scss',
})
export class EntityEditor {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);

    readonly entityId = signal<string | null>(null);
    readonly entities = signal<IEntity[]>([]);
    readonly columns = signal<IColumn[]>([]);
    readonly preview = signal<IEntityRows | null>(null);
    readonly error = signal<string>('');
    readonly busy = signal<boolean>(false);
    readonly roles = ROLES;
    readonly types = TYPES;
    readonly sources = () => this.workspace.getFolder('sources');

    name : string = '';
    kind : EntityKind = 'Item';
    description : string = '';
    sourceId : string | null = null;
    durationFormula : string = '';
    attributes : IAttribute[] = [];

    constructor() {
        effect(() => {
            this.doc().params['reload'];
            const entityId = (this.doc().params['entityTypeId'] as string | undefined) ?? null;
            untracked(() => this.load(entityId));
        });
    }

    async load(entityId : string | null) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.entityId.set(entityId);
        this.preview.set(null);
        try {
            const entities = await this.api.getEntities(profileId);
            this.entities.set(entities);
            const entity = entities.find(x => x.id == entityId);
            this.name = entity?.name ?? '';
            this.kind = entity?.kind ?? 'Item';
            this.description = entity?.description ?? '';
            this.sourceId = entity?.sourceId ?? null;
            this.durationFormula = entity?.durationFormula ?? '';
            this.attributes = (entity?.attributes ?? []).map(x => ({ ...x }));
            await this.loadColumns();
            if(entity?.sourceId)
                await this.loadPreview();
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
    }
    async loadColumns() : Promise<void> {
        const profileId = this.workspace.profileId();
        this.columns.set([]);
        if(!profileId || !this.sourceId)
            return;
        try {
            this.columns.set((await this.api.getSource(profileId, this.sourceId)).columns);
        }
        catch(e) {
        }
    }
    async loadPreview() : Promise<void> {
        const profileId = this.workspace.profileId();
        const entityId = this.entityId();
        if(!profileId || !entityId)
            return;
        try {
            this.preview.set(await this.api.getEntityRows(profileId, entityId));
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }
    previewColumns() : IColumn[] {
        return (this.preview()?.attributes ?? []).map(x => ({ name : x, sqlType : this.attributes.find(y => y.name == x)?.dataType ?? '', nullable : true }));
    }

    addAttribute() : void {
        this.attributes = [...this.attributes, { id : null, name : '', dataType : 'STRING', role : 'Custom', sourceColumn : null, referencesEntityTypeId : null, sortOrder : this.attributes.length + 1 }];
    }
    // One attribute per column not mapped yet, named after it
    addUnmapped() : void {
        const used = new Set(this.attributes.map(x => x.sourceColumn?.toLowerCase()));
        for(const column of this.columns())
            if(!used.has(column.name.toLowerCase()))
                this.attributes.push({ id : null, name : this.toName(column.name), dataType : this.toType(column.sqlType), role : 'Custom', sourceColumn : column.name,
                    referencesEntityTypeId : null, sortOrder : this.attributes.length + 1 });
        this.attributes = [...this.attributes];
    }
    removeAttribute(index : number) : void {
        this.attributes = this.attributes.filter((x, i) => i != index);
    }
    moveAttribute(index : number, by : number) : void {
        const list = [...this.attributes];
        const to = index + by;
        if(to < 0 || to >= list.length)
            return;
        [list[index], list[to]] = [list[to], list[index]];
        this.attributes = list;
    }
    // Picking a column fills in a blank name and its type
    onColumn(attribute : IAttribute) : void {
        const column = this.columns().find(x => x.name == attribute.sourceColumn);
        if(!column)
            return;
        if(!attribute.name)
            attribute.name = this.toName(column.name);
        attribute.dataType = this.toType(column.sqlType);
    }

    async save() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.busy.set(true);
        try {
            const saved = await this.api.saveEntity(profileId, this.entityId(), { name : this.name.trim(), kind : this.kind, description : this.description || null,
                sourceId : this.sourceId || null, durationFormula : this.durationFormula.trim() || null });
            await this.api.saveAttributes(profileId, saved.id, this.attributes.map((x, i) => ({ ...x, sortOrder : i + 1 })));
            this.workspace.toast('Entity saved', 'ok');
            await this.workspace.refreshNavigator();
            if(!this.entityId()) {
                this.workspace.closeDoc(this.doc().id);
                this.workspace.openDoc({ id : `entity:${saved.id}`, kind : 'entity', title : saved.name, icon : 'category', params : { entityTypeId : saved.id } });
            }
            else
                await this.load(saved.id);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    async deleteEntity() : Promise<void> {
        const profileId = this.workspace.profileId();
        const entityId = this.entityId();
        if(!profileId || !entityId)
            return;
        try {
            await this.api.deleteEntity(profileId, entityId);
            await this.workspace.refreshNavigator();
            this.workspace.closeDoc(this.doc().id);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }

    private toName(column : string) : string {
        return column.replace(/([a-z0-9])([A-Z])/g, '$1_$2').replace(/[^A-Za-z0-9_]/g, '_').toLowerCase();
    }
    private toType(sqlType : string) : string {
        const type = sqlType.toLowerCase();
        if(/int/.test(type))
            return 'INTEGER';
        if(/decimal|numeric|float|real|money/.test(type))
            return 'DECIMAL';
        if(/bit/.test(type))
            return 'BOOLEAN';
        if(/date|time/.test(type))
            return 'DATETIME';
        return 'STRING';
    }
}
