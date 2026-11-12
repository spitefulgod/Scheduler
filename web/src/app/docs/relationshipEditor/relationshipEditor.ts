import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IEntity, IGrid, IKeyLabel, IRelationshipDetails, IScaleValue, ISource, IValueScale, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';

// A cell's value and where it comes from: typed in, the source, or the default
interface ICell {
    value : string;
    from : 'typed' | 'source' | 'default' | 'edit';
}
interface IListRow {
    a : string;
    b : string;
    value : string;
    from : ICell['from'];
}

const SEP = '\u0001';
// The rule blocks a relationship can feed
const DRIVES = ['Allowed', 'Compatible', 'CapacityMatrix', 'Configurations', 'Changeover', 'Alternatives'];

// The pivot of one relationship, A down the side and B across, cells coloured by their scale value, select a range and set it in one go,
// or the same values as a list for large sparse data, plus the relationship's own settings
@Component({
    selector : 'app-relationship-editor',
    imports : [FormsModule],
    templateUrl : './relationshipEditor.html',
    styleUrl : './relationshipEditor.scss',
})
export class RelationshipEditor {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);

    readonly relationshipId = signal<string | null>(null);
    readonly grid = signal<IGrid | null>(null);
    readonly scales = signal<IValueScale[]>([]);
    readonly entities = signal<IEntity[]>([]);
    readonly sourceColumns = signal<string[]>([]);
    readonly error = signal<string>('');
    readonly busy = signal<boolean>(false);
    readonly showSettings = signal<boolean>(false);
    readonly mode = signal<'Matrix' | 'List'>('Matrix');
    // Changes not saved yet, a blank value clears a typed in value
    readonly edits = signal<Map<string, string>>(new Map());
    readonly selection = signal<Set<string>>(new Set());
    readonly rowFilter = signal<string>('');
    readonly columnFilter = signal<string>('');
    readonly listFilter = signal<string>('');
    private anchor : { row : number; column : number } | null = null;
    numberValue : string = '';
    newPair : { a : string; b : string; value : string } = { a : '', b : '', value : '' };

    settings : IRelationshipDetails = this.blankSettings();
    readonly drives = DRIVES;

    readonly scale = computed<IValueScale | null>(() => this.scales().find(x => x.id == this.grid()?.relationship.valueScaleId) ?? null);
    readonly isNumeric = computed<boolean>(() => this.scale()?.values.some(x => x.meaning == 'Numeric') ?? false);
    readonly rows = computed<IKeyLabel[]>(() => this.filter(this.grid()?.a ?? [], this.rowFilter()));
    readonly columns = computed<IKeyLabel[]>(() => this.filter(this.grid()?.b ?? [], this.columnFilter()));
    readonly sources = computed(() => this.workspace.getFolder('sources'));
    // Every pair's saved value, both ways round for a symmetric one
    readonly saved = computed<Map<string, ICell>>(() => {
        const map = new Map<string, ICell>();
        const grid = this.grid();
        if(!grid)
            return map;
        for(const value of grid.values) {
            const cell : ICell = { value : value.value, from : value.typed ? 'typed' : 'source' };
            map.set(value.a + SEP + value.b, cell);
            if(grid.relationship.symmetric && !map.has(value.b + SEP + value.a))
                map.set(value.b + SEP + value.a, cell);
        }
        return map;
    });
    readonly listRows = computed<IListRow[]>(() => {
        const grid = this.grid();
        if(!grid)
            return [];
        const rows = new Map<string, IListRow>();
        for(const value of grid.values)
            rows.set(value.a + SEP + value.b, { a : value.a, b : value.b, value : value.value, from : value.typed ? 'typed' : 'source' });
        for(const [key, value] of this.edits()) {
            const [a, b] = key.split(SEP);
            rows.set(key, { a : a, b : b, value : value, from : 'edit' });
        }
        const filter = this.listFilter().trim().toLowerCase();
        return [...rows.values()].filter(x => !filter || `${x.a} ${x.b} ${x.value}`.toLowerCase().includes(filter))
            .sort((x, y) => x.a.localeCompare(y.a) || x.b.localeCompare(y.b));
    });
    readonly editCount = computed<number>(() => this.edits().size);

    constructor() {
        effect(() => {
            this.doc().params['reload'];
            const relationshipId = (this.doc().params['relationshipId'] as string | undefined) ?? null;
            untracked(() => this.load(relationshipId));
        });
    }

    async load(relationshipId : string | null) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.relationshipId.set(relationshipId);
        this.error.set('');
        this.edits.set(new Map());
        this.selection.set(new Set());
        try {
            const [scales, entities] = await Promise.all([this.api.getValueScales(profileId), this.api.getEntities(profileId)]);
            this.scales.set(scales);
            this.entities.set(entities);
            if(relationshipId) {
                const grid = await this.api.getGrid(profileId, relationshipId);
                this.grid.set(grid);
                this.mode.set(grid.relationship.editor);
                const r = grid.relationship;
                this.settings = { name : r.name, entityTypeAId : r.entityTypeAId, entityTypeBId : r.entityTypeBId, valueScaleId : r.valueScaleId, defaultValue : r.defaultValue,
                    symmetric : r.symmetric, editor : r.editor, drives : r.drives, sourceId : r.sourceId, sourceAColumn : r.sourceAColumn, sourceBColumn : r.sourceBColumn,
                    sourceValueColumn : r.sourceValueColumn };
                await this.loadColumns();
            }
            else {
                this.grid.set(null);
                this.settings = this.blankSettings();
                this.settings.editor = (this.doc().params['editor'] as 'Matrix' | 'List' | undefined) ?? 'Matrix';
                this.showSettings.set(true);
            }
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
    }
    async loadColumns() : Promise<void> {
        const profileId = this.workspace.profileId();
        this.sourceColumns.set([]);
        if(!profileId || !this.settings.sourceId)
            return;
        try {
            const source : ISource = await this.api.getSource(profileId, this.settings.sourceId);
            this.sourceColumns.set(source.columns.map(x => x.name));
        }
        catch(e) {
        }
    }

    getCell(a : string, b : string) : ICell {
        const edit = this.edits().get(a + SEP + b);
        if(edit !== undefined)
            return edit === '' ? { value : this.saved().get(a + SEP + b)?.from == 'source' ? this.saved().get(a + SEP + b)!.value : this.defaultValue(), from : 'edit' } : { value : edit, from : 'edit' };
        return this.saved().get(a + SEP + b) ?? { value : this.defaultValue(), from : 'default' };
    }
    getScaleValue(code : string) : IScaleValue | undefined {
        return this.scale()?.values.find(x => x.code == code);
    }
    getColour(code : string) : string | null {
        if(this.isNumeric())
            return null;
        return this.getScaleValue(code)?.colour ?? null;
    }
    getTitle(a : IKeyLabel, b : IKeyLabel) : string {
        const cell = this.getCell(a.key, b.key);
        const value = this.getScaleValue(cell.value);
        const from = { typed : 'typed in', source : 'from the source', default : 'the default', edit : 'not saved yet' }[cell.from];
        return `${a.label} × ${b.label}: ${value?.label ?? cell.value} (${from})`;
    }
    isSelected(a : string, b : string) : boolean {
        return this.selection().has(a + SEP + b);
    }

    // Click picks a cell, shift click a range, ctrl click adds one
    onCellDown(event : MouseEvent, row : number, column : number) : void {
        const rows = this.rows();
        const columns = this.columns();
        const key = rows[row].key + SEP + columns[column].key;
        if(event.shiftKey && this.anchor) {
            const set = new Set<string>(event.ctrlKey || event.metaKey ? this.selection() : []);
            for(let r = Math.min(this.anchor.row, row); r <= Math.max(this.anchor.row, row); r++)
                for(let c = Math.min(this.anchor.column, column); c <= Math.max(this.anchor.column, column); c++)
                    set.add(rows[r].key + SEP + columns[c].key);
            this.selection.set(set);
            return;
        }
        if(event.ctrlKey || event.metaKey) {
            const set = new Set(this.selection());
            if(set.has(key))
                set.delete(key);
            else
                set.add(key);
            this.selection.set(set);
        }
        else
            this.selection.set(new Set([key]));
        this.anchor = { row : row, column : column };
    }
    // Double click steps the cell to the next value on the scale
    onCellDouble(a : string, b : string) : void {
        const values = this.scale()?.values.filter(x => x.meaning != 'Numeric') ?? [];
        if(!values.length)
            return;
        const index = values.findIndex(x => x.code == this.getCell(a, b).value);
        this.setValues([a + SEP + b], values[(index + 1) % values.length].code);
    }
    selectRow(a : string) : void {
        this.selection.set(new Set(this.columns().map(x => a + SEP + x.key)));
    }
    selectColumn(b : string) : void {
        this.selection.set(new Set(this.rows().map(x => x.key + SEP + b)));
    }
    selectAll() : void {
        const set = new Set<string>();
        for(const a of this.rows())
            for(const b of this.columns())
                set.add(a.key + SEP + b.key);
        this.selection.set(set);
    }
    // Sets the selected cells, blank puts them back to the source or the default
    fill(code : string) : void {
        this.setValues([...this.selection()], code);
    }
    onKey(event : KeyboardEvent) : void {
        if(!this.selection().size || event.target instanceof HTMLInputElement || event.target instanceof HTMLSelectElement)
            return;
        if(event.key == 'Delete' || event.key == 'Backspace') {
            this.fill('');
            event.preventDefault();
            return;
        }
        const value = this.scale()?.values.find(x => x.code.toLowerCase() == event.key.toLowerCase() && x.meaning != 'Numeric');
        if(value) {
            this.fill(value.code);
            event.preventDefault();
        }
    }

    setListValue(row : IListRow, value : string) : void {
        this.setValues([row.a + SEP + row.b], value);
    }
    addPair() : void {
        if(!this.newPair.a || !this.newPair.b || !this.newPair.value)
            return;
        this.setValues([this.newPair.a + SEP + this.newPair.b], this.newPair.value);
        this.newPair = { a : this.newPair.a, b : '', value : this.newPair.value };
    }

    async save() : Promise<void> {
        const profileId = this.workspace.profileId();
        const relationshipId = this.relationshipId();
        if(!profileId || !relationshipId || !this.edits().size)
            return;
        this.busy.set(true);
        try {
            const values = [...this.edits()].map(([key, value]) => {
                const [a, b] = key.split(SEP);
                return { aKey : a, bKey : b, value : value };
            });
            await this.api.saveValues(profileId, relationshipId, values);
            this.workspace.toast(`Saved ${values.length} values`, 'ok');
            await this.workspace.refreshNavigator();
            await this.load(relationshipId);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    discardEdits() : void {
        this.edits.set(new Map());
    }
    // Copies the source's values in as typed in values, so the matrix no longer needs the source
    async importValues() : Promise<void> {
        const profileId = this.workspace.profileId();
        const relationshipId = this.relationshipId();
        if(!profileId || !relationshipId)
            return;
        this.busy.set(true);
        try {
            await this.api.importValues(profileId, relationshipId);
            this.workspace.toast('Copied the source values in', 'ok');
            await this.load(relationshipId);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    async saveSettings() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.busy.set(true);
        try {
            const saved = await this.api.saveRelationship(profileId, this.relationshipId(), { ...this.settings, sourceId : this.settings.sourceId || null });
            this.workspace.toast('Relationship saved', 'ok');
            await this.workspace.refreshNavigator();
            if(!this.relationshipId())
                this.workspace.closeDoc(this.doc().id);
            this.workspace.openDoc({ id : `relationship:${saved.id}`, kind : 'relationship', title : saved.name, icon : 'grid_on', params : { relationshipId : saved.id, reload : Date.now() } });
            this.showSettings.set(false);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    async deleteRelationship() : Promise<void> {
        const profileId = this.workspace.profileId();
        const relationshipId = this.relationshipId();
        if(!profileId || !relationshipId)
            return;
        try {
            await this.api.deleteRelationship(profileId, relationshipId);
            await this.workspace.refreshNavigator();
            this.workspace.closeDoc(this.doc().id);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }
    scaleFor(scaleId : string) : IValueScale | undefined {
        return this.scales().find(x => x.id == scaleId);
    }
    labelFor(code : string) : string {
        return this.getScaleValue(code)?.label ?? code;
    }

    private defaultValue() : string {
        return this.grid()?.relationship.defaultValue ?? '';
    }
    private setValues(keys : string[], value : string) : void {
        const symmetric = this.grid()?.relationship.symmetric ?? false;
        const edits = new Map(this.edits());
        for(const key of keys) {
            edits.set(key, value);
            // a symmetric pair keeps one value, whichever way round it's kept
            if(symmetric) {
                const [a, b] = key.split(SEP);
                if(a != b && this.saved().has(b + SEP + a) && !this.grid()!.values.some(x => x.a == a && x.b == b))
                    edits.set(b + SEP + a, value);
            }
        }
        this.edits.set(edits);
    }
    private filter(list : IKeyLabel[], text : string) : IKeyLabel[] {
        const filter = text.trim().toLowerCase();
        return filter ? list.filter(x => `${x.key} ${x.label}`.toLowerCase().includes(filter)) : list;
    }
    private blankSettings() : IRelationshipDetails {
        return { name : '', entityTypeAId : '', entityTypeBId : '', valueScaleId : 'BOOLEAN', defaultValue : null, symmetric : false, editor : 'Matrix', drives : null,
            sourceId : null, sourceAColumn : null, sourceBColumn : null, sourceValueColumn : null };
    }
}
