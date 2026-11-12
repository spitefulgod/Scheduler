import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, ICalendar, ICalendarException, IEntity, IShift, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

// A calendar's week of shifts and its exceptions, e.g. a shutdown or a press out for maintenance,
// a resource's calendar inherits its parent's, e.g. the site's
@Component({
    selector : 'app-calendar-editor',
    imports : [FormsModule],
    templateUrl : './calendarEditor.html',
    styleUrl : './calendarEditor.scss',
})
export class CalendarEditor {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);

    readonly calendarId = signal<string | null>(null);
    readonly calendars = signal<ICalendar[]>([]);
    readonly resourceEntities = signal<IEntity[]>([]);
    readonly error = signal<string>('');
    readonly busy = signal<boolean>(false);
    readonly days = DAYS;
    // Monday first
    readonly order = [1, 2, 3, 4, 5, 6, 0];

    name : string = '';
    parentCalendarId : string | null = null;
    resourceEntityTypeId : string | null = null;
    resourceKey : string = '';
    shifts : IShift[] = [];
    exceptions : ICalendarException[] = [];

    constructor() {
        effect(() => {
            this.doc().params['reload'];
            const calendarId = (this.doc().params['calendarId'] as string | undefined) ?? null;
            untracked(() => this.load(calendarId));
        });
    }

    async load(calendarId : string | null) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.calendarId.set(calendarId);
        try {
            const [calendars, entities] = await Promise.all([this.api.getCalendars(profileId), this.api.getEntities(profileId)]);
            this.calendars.set(calendars);
            this.resourceEntities.set(entities.filter(x => x.kind == 'Resource'));
            const calendar = calendars.find(x => x.id == calendarId);
            this.name = calendar?.name ?? '';
            this.parentCalendarId = calendar?.parentCalendarId ?? null;
            this.resourceEntityTypeId = calendar?.resourceEntityTypeId ?? null;
            this.resourceKey = calendar?.resourceKey ?? '';
            this.shifts = (calendar?.shifts ?? []).map(x => ({ ...x }));
            this.exceptions = (calendar?.exceptions ?? []).map(x => ({ ...x, startTime : x.startTime.substring(0, 16), endTime : x.endTime.substring(0, 16) }));
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
    }

    shiftsOn(day : number) : IShift[] {
        return this.shifts.filter(x => x.dayOfWeek == day);
    }
    addShift(day : number) : void {
        this.shifts = [...this.shifts, { dayOfWeek : day, startTime : '06:00', endTime : '14:00' }];
    }
    removeShift(shift : IShift) : void {
        this.shifts = this.shifts.filter(x => x != shift);
    }
    // Copies Monday's shifts to the other weekdays
    copyMonday() : void {
        const monday = this.shiftsOn(1);
        this.shifts = [...this.shifts.filter(x => x.dayOfWeek < 1 || x.dayOfWeek > 5 || x.dayOfWeek == 1),
            ...[2, 3, 4, 5].flatMap(day => monday.map(x => ({ ...x, dayOfWeek : day })))];
    }
    // Where a shift sits on a 24 hour bar, an end at or before the start runs into the next day
    bars(day : number) : { left : number; width : number }[] {
        const bars : { left : number; width : number }[] = [];
        for(const shift of this.shiftsOn(day)) {
            const start = this.minutes(shift.startTime);
            let end = this.minutes(shift.endTime);
            if(end <= start)
                end = 1440;
            bars.push({ left : start / 14.4, width : (end - start) / 14.4 });
        }
        // the day before's shift that runs past midnight
        const before = (day + 6) % 7;
        for(const shift of this.shiftsOn(before)) {
            const start = this.minutes(shift.startTime);
            const end = this.minutes(shift.endTime);
            if(end <= start && end > 0)
                bars.push({ left : 0, width : end / 14.4 });
        }
        return bars;
    }
    addException() : void {
        const start = new Date();
        start.setHours(0, 0, 0, 0);
        const text = (date : Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}T00:00`;
        this.exceptions = [...this.exceptions, { startTime : text(start), endTime : text(new Date(start.getTime() + 86400000)), kind : 'Down', reason : '' }];
    }
    removeException(index : number) : void {
        this.exceptions = this.exceptions.filter((x, i) => i != index);
    }

    async save() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(!profileId)
            return;
        this.busy.set(true);
        try {
            const saved = await this.api.saveCalendar(profileId, this.calendarId(), {
                name : this.name.trim(),
                parentCalendarId : this.parentCalendarId,
                resourceEntityTypeId : this.resourceEntityTypeId,
                resourceKey : this.resourceKey.trim() || null,
                shifts : this.shifts,
                exceptions : this.exceptions.map(x => ({ ...x, reason : x.reason || null }))
            });
            this.workspace.toast('Calendar saved', 'ok');
            await this.workspace.refreshNavigator();
            if(!this.calendarId()) {
                this.workspace.closeDoc(this.doc().id);
                this.workspace.openDoc({ id : `calendar:${saved.id}`, kind : 'calendar', title : saved.name, icon : 'calendar_month', params : { calendarId : saved.id } });
            }
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
    async deleteCalendar() : Promise<void> {
        const profileId = this.workspace.profileId();
        const calendarId = this.calendarId();
        if(!profileId || !calendarId)
            return;
        try {
            await this.api.deleteCalendar(profileId, calendarId);
            await this.workspace.refreshNavigator();
            this.workspace.closeDoc(this.doc().id);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }

    private minutes(time : string) : number {
        const [hours, minutes] = time.split(':').map(x => Number(x));
        return (hours || 0) * 60 + (minutes || 0);
    }
}
