import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IEntity, IRelationship, IRule, IRuleCheck, IRuleDetails, IRuleParameter, IRuleTypeDetails, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';

interface ICategory {
    name : string;
    types : IRuleTypeDetails[];
}
// A name a formula can use, e.g. item.qty
interface IName {
    text : string;
    title : string;
}

// Builds a rule from chained blocks: who it's for, when, must or should, the block itself and its parameters,
// pick or drag a block from the palette, the parameters' editors come from the block's metadata
@Component({
    selector : 'app-rule-editor',
    imports : [FormsModule],
    templateUrl : './ruleEditor.html',
    styleUrl : './ruleEditor.scss',
})
export class RuleEditor {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);

    readonly ruleId = signal<string | null>(null);
    readonly types = signal<IRuleTypeDetails[]>([]);
    readonly entities = signal<IEntity[]>([]);
    readonly relationships = signal<IRelationship[]>([]);
    readonly rules = signal<IRule[]>([]);
    readonly check = signal<IRuleCheck | null>(null);
    readonly error = signal<string>('');
    readonly busy = signal<boolean>(false);
    readonly dropping = signal<boolean>(false);
    // The field a name chip goes into
    readonly focusField = signal<string>('when');
    readonly typeId = signal<string>('');
    readonly whoEntity = signal<string | null>(null);

    name : string = '';
    enabled : boolean = true;
    strength : 'Hard' | 'Soft' = 'Hard';
    weight : number = 10;
    when : string = '';
    params : {[name : string] : string | null} = {};

    readonly type = computed<IRuleTypeDetails | null>(() => this.types().find(x => x.id == this.typeId()) ?? null);
    readonly categories = computed<ICategory[]>(() => {
        const map = new Map<string, IRuleTypeDetails[]>();
        for(const type of this.types())
            map.set(type.category, [...(map.get(type.category) ?? []), type]);
        return [...map].map(([name, types]) => ({ name : name, types : types }));
    });
    readonly itemEntities = computed<IEntity[]>(() => this.entities().filter(x => x.kind == 'Item'));
    // What the other rules say items run on and draw from, so formulas can name them
    readonly resourceEntity = computed<string | null>(() => this.findParam('RunsOn', 'resource') ?? this.entities().find(x => x.kind == 'Resource')?.name ?? null);
    readonly toolingEntity = computed<string | null>(() => this.findParam('UsesTooling', 'tooling'));
    readonly names = computed<IName[]>(() => {
        const names : IName[] = [];
        const add = (prefix : string, entityName : string | null) => {
            const entity = this.entities().find(x => x.name == entityName);
            if(!entity)
                return;
            names.push({ text : `${prefix}.key`, title : `${entity.name}'s key` });
            for(const attribute of entity.attributes)
                names.push({ text : `${prefix}.${attribute.name}`, title : `${entity.name}.${attribute.name}, ${attribute.role}, ${attribute.dataType}` });
        };
        add('item', this.whoEntity() ?? this.itemEntities()[0]?.name ?? null);
        add('resource', this.resourceEntity());
        add('tooling', this.toolingEntity());
        return names;
    });
    readonly ruleStat = computed<{ items : number; modesRemoved : number } | null>(() => {
        const ruleId = this.ruleId();
        return ruleId ? this.check()?.rules[ruleId] ?? null : null;
    });
    readonly ruleDiagnostics = computed(() => (this.check()?.diagnostics ?? []).filter(x => !x.ruleId || x.ruleId == this.ruleId()).slice(0, 30));

    constructor() {
        effect(() => {
            this.doc().params['reload'];
            const ruleId = (this.doc().params['ruleId'] as string | undefined) ?? null;
            untracked(() => this.load(ruleId));
        });
    }

    async load(ruleId : string | null) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.ruleId.set(ruleId);
        this.check.set(null);
        try {
            const [types, entities, relationships, rules] = await Promise.all([this.api.getRuleTypeDetails(), this.api.getEntities(profileId),
                this.api.getRelationships(profileId), this.api.getRules(profileId)]);
            this.types.set(types);
            this.entities.set(entities);
            this.relationships.set(relationships);
            this.rules.set(rules);
            const rule = rules.find(x => x.id == ruleId);
            if(rule) {
                this.name = rule.name;
                this.enabled = rule.enabled;
                this.strength = rule.strength;
                this.weight = rule.weight;
                this.whoEntity.set(rule.blocks.who?.entity ?? null);
                this.when = rule.blocks.who?.when ?? '';
                this.params = { ...rule.blocks.params };
                this.typeId.set(rule.ruleTypeId);
            }
            else {
                this.name = '';
                this.whoEntity.set(this.itemEntities()[0]?.name ?? null);
                this.when = '';
                this.params = {};
                this.typeId.set('');
                const typeId = this.doc().params['ruleTypeId'] as string | undefined;
                if(typeId)
                    this.pick(typeId);
            }
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
    }

    // Puts a block in the builder, the parameters it shares with the last one are kept
    pick(typeId : string) : void {
        const type = this.types().find(x => x.id == typeId);
        if(!type)
            return;
        this.typeId.set(typeId);
        const params : {[name : string] : string | null} = {};
        for(const parameter of type.parameters)
            params[parameter.name] = this.params[parameter.name] ?? parameter.default ?? null;
        this.params = params;
        if(!type.supportsHard)
            this.strength = 'Soft';
        else if(!type.supportsSoft)
            this.strength = 'Hard';
        if(!this.name)
            this.name = type.name;
    }
    onDragStart(event : DragEvent, typeId : string) : void {
        event.dataTransfer?.setData('text/plain', typeId);
    }
    onDrop(event : DragEvent) : void {
        event.preventDefault();
        this.dropping.set(false);
        const typeId = event.dataTransfer?.getData('text/plain');
        if(typeId)
            this.pick(typeId);
    }
    // Adds a name to the formula being typed
    insertName(name : string) : void {
        const field = this.focusField();
        if(field == 'when')
            this.when = this.join(this.when, name);
        else
            this.params[field] = this.join(this.params[field] ?? '', name);
    }
    entitiesFor(parameter : IRuleParameter) : IEntity[] {
        return parameter.kind ? this.entities().filter(x => x.kind == parameter.kind) : this.entities();
    }
    getHelp(parameter : IRuleParameter) : string {
        const scope = parameter.scope == 'mode' ? 'item, resource and tooling' : parameter.scope ?? '';
        return [parameter.help, scope && (parameter.type == 'expression' || parameter.type == 'condition') ? `Can use ${scope} names` : ''].filter(x => x).join('. ');
    }

    async save() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId || !this.type())
            return;
        const params : {[name : string] : string | null} = {};
        for(const [key, value] of Object.entries(this.params))
            if(value !== null && String(value).trim() !== '')
                params[key] = String(value).trim();
        const details : IRuleDetails = {
            name : this.name.trim(),
            ruleTypeId : this.typeId(),
            enabled : this.enabled,
            strength : this.strength,
            weight : this.strength == 'Soft' ? Number(this.weight) || 0 : 0,
            blocks : { who : this.whoEntity() || this.when.trim() ? { entity : this.whoEntity(), when : this.when.trim() || null } : null, params : params },
            sortOrder : null
        };
        this.busy.set(true);
        try {
            const saved = await this.api.saveRule(profileId, this.ruleId(), details);
            this.workspace.toast('Rule saved', 'ok');
            await this.workspace.refreshNavigator();
            if(!this.ruleId()) {
                this.workspace.closeDoc(this.doc().id);
                this.workspace.openDoc({ id : `rule:${saved.id}`, kind : 'rule', title : saved.name, icon : 'rule', params : { ruleId : saved.id } });
            }
            else
                await this.runCheck();
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    // Runs the rule engine on the data without solving, for how many items the rule touches and anything wrong
    async runCheck() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.busy.set(true);
        try {
            this.check.set(await this.api.checkRules(profileId));
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    async deleteRule() : Promise<void> {
        const profileId = this.workspace.profileId();
        const ruleId = this.ruleId();
        if(!profileId || !ruleId)
            return;
        try {
            await this.api.deleteRule(profileId, ruleId);
            await this.workspace.refreshNavigator();
            this.workspace.closeDoc(this.doc().id);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }

    private findParam(typeId : string, name : string) : string | null {
        return this.rules().find(x => x.ruleTypeId == typeId && x.enabled && x.blocks.params?.[name])?.blocks.params[name] ?? null;
    }
    private join(text : string, name : string) : string {
        return text && !/[\s(]$/.test(text) ? `${text} ${name}` : `${text}${name}`;
    }
}
