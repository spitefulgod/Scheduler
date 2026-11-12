import { Component, computed, effect, inject, input, OnDestroy, signal, untracked } from '@angular/core';
import { KeyValuePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IAssignment, IItemPin, IPlanLock, ISolveRequest, ISolveRun, RunKind, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';
import { formatTime, GanttChart, IGanttMove, toDate, toLocalText } from '../ganttChart/ganttChart';

type Panel = 'changes' | 'item' | 'unscheduled' | 'checks' | 'locks';

// What an emergency run is told: the resource that's down, for how long, and where its work may go
interface IEmergency {
    resourceKey : string;
    from : string;
    to : string;
    reason : string;
    alternatives : {[key : string] : boolean};
}

const RUNNING = ['Pending', 'Running'];

// A run, or the published plan: the Gantt or table, what changed against the plan, and the what-if, emergency, publish and lock actions
@Component({
    selector : 'app-schedule-doc',
    imports : [FormsModule, KeyValuePipe, GanttChart],
    templateUrl : './scheduleDoc.html',
    styleUrl : './scheduleDoc.scss',
})
export class ScheduleDoc implements OnDestroy {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    readonly workspace = inject(WorkspaceService);
    private timer : ReturnType<typeof setTimeout> | null = null;

    readonly run = signal<ISolveRun | null>(null);
    readonly publishedId = signal<string | null>(null);
    readonly loading = signal<boolean>(true);
    readonly error = signal<string>('');
    readonly busy = signal<boolean>(false);
    readonly locks = signal<IPlanLock[]>([]);
    readonly panel = signal<Panel>('changes');
    readonly selectedKey = signal<string | null>(null);
    readonly hourWidth = signal<number>(6);
    readonly colourBy = signal<string | null>(null);
    // What-if moves waiting to be run, by item
    readonly moves = signal<IItemPin[]>([]);
    readonly emergency = signal<IEmergency | null>(null);
    readonly tableFilter = signal<string>('');
    lockHours : number = 24;
    lockReason : string = '';

    readonly isRunning = computed<boolean>(() => RUNNING.includes(this.run()?.status ?? ''));
    readonly assignments = computed<IAssignment[]>(() => this.run()?.assignments ?? []);
    readonly isPublished = computed<boolean>(() => this.run()?.published ?? false);
    // A finished run that isn't the plan can be accepted or thrown away
    readonly canDecide = computed<boolean>(() => {
        const run = this.run();
        return run != null && !run.published && !run.discarded && ['Optimal', 'Feasible'].includes(run.status);
    });
    readonly movedKeys = computed<string[]>(() => this.moves().map(x => x.itemKey));
    readonly attributeNames = computed<string[]>(() => {
        const names = new Set<string>();
        for(const assignment of this.assignments().slice(0, 50))
            for(const name of Object.keys(assignment.attributes ?? {}))
                names.add(name);
        return [...names];
    });
    readonly selectedRows = computed<IAssignment[]>(() => this.assignments().filter(x => x.itemKey == this.selectedKey()));
    // The latest a window lock reaches, drawn as the locked shading
    readonly lockedUntil = computed<string | null>(() => {
        const times = this.locks().filter(x => x.kind == 'Window' && !x.resourceKey && x.lockedUntil).map(x => x.lockedUntil!);
        const hours = Number(this.workspace.profile()?.settings?.lockHours ?? 0);
        if(hours > 0)
            times.push(toLocalText(new Date(Date.now() + hours * 3600000)));
        return times.length ? times.sort().at(-1)! : null;
    });
    readonly tableRows = computed<IAssignment[]>(() => {
        const filter = this.tableFilter().trim().toLowerCase();
        const rows = this.assignments().slice().sort((a, b) => a.resourceKey.localeCompare(b.resourceKey) || a.startTime.localeCompare(b.startTime));
        return filter ? rows.filter(x => `${x.itemKey} ${x.itemLabel} ${x.resourceKey} ${x.poolKey}`.toLowerCase().includes(filter)) : rows;
    });
    readonly statsList = computed<[string, string][]>(() => {
        const stats = this.run()?.stats;
        if(!stats)
            return [];
        const names : {[name : string] : string} = { items : 'Items', planned : 'Planned', locked : 'Locked', unscheduled : 'Unscheduled', late : 'Late', lateHours : 'Hours late', wallSeconds : 'Seconds' };
        return Object.entries(names).filter(([key]) => stats[key] != null).map(([key, label]) => [label, String(stats[key])]);
    });

    readonly formatTime = formatTime;

    constructor() {
        effect(() => {
            this.doc().params['reload'];
            const runId = this.doc().params['runId'] as string | undefined;
            untracked(() => this.load(runId ?? null));
        });
    }
    ngOnDestroy() : void {
        this.stopPolling();
    }

    async load(runId : string | null) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.stopPolling();
        this.loading.set(true);
        this.error.set('');
        this.moves.set([]);
        try {
            const plan = await this.api.getPlan(profileId);
            const published = 'id' in plan ? plan as ISolveRun : null;
            this.publishedId.set(published?.id ?? null);
            const run = runId && runId != published?.id ? await this.api.getRun(profileId, runId) : published;
            this.setRun(run);
            this.locks.set(await this.api.getLocks(profileId));
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
        this.loading.set(false);
    }

    // Starts a run, what-if and emergency runs carry their moves or the resource that's down
    async start(kind : RunKind) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        const request : ISolveRequest = { kind : kind };
        if(kind == 'WhatIf')
            request.pins = this.moves();
        const emergency = this.emergency();
        if(kind == 'Emergency' && emergency) {
            request.downs = [{ resourceKey : emergency.resourceKey, from : emergency.from, to : emergency.to, reason : emergency.reason || 'Breakdown' }];
            request.alternatives = Object.entries(emergency.alternatives).filter(([, on]) => on).map(([key]) => key);
        }
        this.busy.set(true);
        try {
            const run = await this.api.solve(profileId, request);
            this.emergency.set(null);
            this.moves.set([]);
            this.panel.set('changes');
            this.setRun(run);
            await this.workspace.refreshNavigator();
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    async stop() : Promise<void> {
        const run = this.run();
        const profileId = this.workspace.profileId();
        if(run && profileId)
            await this.api.stopRun(profileId, run.id);
    }
    // Makes this run the plan
    async publish() : Promise<void> {
        const run = this.run();
        const profileId = this.workspace.profileId();
        if(!run || !profileId)
            return;
        this.busy.set(true);
        try {
            await this.api.publishRun(profileId, run.id);
            this.workspace.toast('Published, this is now the plan', 'ok');
            await this.workspace.refreshNavigator();
            await this.load(run.id);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    // Throws the run away and goes back to the plan
    async discard() : Promise<void> {
        const run = this.run();
        const profileId = this.workspace.profileId();
        if(!run || !profileId)
            return;
        this.busy.set(true);
        try {
            await this.api.discardRun(profileId, run.id);
            this.workspace.toast('Discarded, the plan is unchanged', 'info');
            await this.workspace.refreshNavigator();
            await this.load(null);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }

    onMoved(move : IGanttMove) : void {
        this.moves.update(x => [...x.filter(y => y.itemKey != move.itemKey), { itemKey : move.itemKey, resourceKey : move.resourceKey, start : move.start }]);
        this.selectedKey.set(move.itemKey);
        this.panel.set('changes');
    }
    removeMove(itemKey : string) : void {
        this.moves.update(x => x.filter(y => y.itemKey != itemKey));
    }
    onSelected(itemKey : string | null) : void {
        this.selectedKey.set(itemKey);
        if(itemKey)
            this.panel.set('item');
    }
    zoom(factor : number) : void {
        this.hourWidth.update(x => Math.min(120, Math.max(1, x * factor)));
    }

    openEmergency() : void {
        const resources = this.run()?.details?.resources ?? [];
        const from = new Date();
        from.setMinutes(0, 0, 0);
        this.emergency.set({
            resourceKey : resources[0]?.key ?? '',
            from : toLocalText(from).substring(0, 16),
            to : toLocalText(new Date(from.getTime() + 8 * 3600000)).substring(0, 16),
            reason : 'Breakdown',
            alternatives : {}
        });
    }
    updateEmergency(change : Partial<IEmergency>) : void {
        this.emergency.update(x => x ? { ...x, ...change } : x);
    }
    toggleAlternative(key : string, on : boolean) : void {
        this.emergency.update(x => x ? { ...x, alternatives : { ...x.alternatives, [key] : on } } : x);
    }

    // Locks from now for the hours given, or the selected item
    async addWindowLock() : Promise<void> {
        await this.addLock({ kind : 'Window', lockedUntil : toLocalText(new Date(Date.now() + this.lockHours * 3600000)), resourceKey : null, itemKey : null, reason : this.lockReason || null });
    }
    async lockItem(itemKey : string) : Promise<void> {
        await this.addLock({ kind : 'Item', lockedUntil : null, resourceKey : null, itemKey : itemKey, reason : this.lockReason || null });
    }
    async deleteLock(lockId : string) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        try {
            await this.api.deleteLock(profileId, lockId);
            this.locks.set(await this.api.getLocks(profileId));
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }
    isItemLocked(itemKey : string) : boolean {
        return this.locks().some(x => x.kind == 'Item' && x.itemKey == itemKey);
    }

    hours(minutes : number | null | undefined) : string {
        return minutes ? `${Math.round(minutes / 6) / 10} h` : '';
    }
    shift(minutes : number | null) : string {
        if(minutes == null)
            return '';
        const hours = Math.round(minutes / 6) / 10;
        return hours > 0 ? `+${hours} h` : `${hours} h`;
    }
    statusTone(status : string) : string {
        return status == 'Optimal' || status == 'Feasible' ? 'ok' : status == 'Infeasible' || status == 'Failed' || status == 'Invalid' ? 'error' : '';
    }
    elapsed(run : ISolveRun) : string {
        if(!run.started)
            return '';
        const seconds = Math.round(((run.finished ? toDate(run.finished) : new Date()).getTime() - toDate(run.started).getTime()) / 1000);
        return `${seconds}s of ${run.timeLimitSeconds}s`;
    }

    private async addLock(details : Parameters<SchedulerApi['addLock']>[1]) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        try {
            await this.api.addLock(profileId, details);
            this.locks.set(await this.api.getLocks(profileId));
            this.workspace.toast('Locked, runs leave it alone', 'ok');
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }
    private setRun(run : ISolveRun | null) : void {
        this.run.set(run);
        this.workspace.currentRunId.set(run?.id ?? null);
        this.workspace.solving.set(RUNNING.includes(run?.status ?? ''));
        if(run && this.colourBy() == null)
            this.colourBy.set(run.details?.colourBy ?? this.workspace.profile()?.settings?.colourBy as string ?? null);
        if(run?.changes?.summary?.hasBaseline === false || run?.published)
            this.panel.update(x => x == 'changes' && !this.moves().length ? 'unscheduled' : x);
        if(run && RUNNING.includes(run.status))
            this.timer = setTimeout(() => this.poll(run.id), 1500);
        else if(run?.finished)
            this.workspace.lastSolve.set(`${formatTime(run.finished)} ${run.status}`);
    }
    private async poll(runId : string) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId || this.run()?.id != runId)
            return;
        try {
            const run = await this.api.getRun(profileId, runId);
            this.setRun(run);
            if(!RUNNING.includes(run.status)) {
                this.panel.set(run.changes?.summary?.hasBaseline ? 'changes' : 'unscheduled');
                await this.workspace.refreshNavigator();
            }
        }
        catch(e) {
            this.timer = setTimeout(() => this.poll(runId), 3000);
        }
    }
    private stopPolling() : void {
        if(this.timer)
            clearTimeout(this.timer);
        this.timer = null;
    }
}
