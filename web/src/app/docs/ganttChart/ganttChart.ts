import { Component, computed, ElementRef, input, output, signal, viewChild } from '@angular/core';
import { IAssignment, IConfigurationRange, IDownRange, IRunDetails } from '../../api/schedulerApi';

// A bar on a lane, row is the sub row it's stacked on when bars overlap
interface IBar {
    assignment : IAssignment;
    left : number;
    width : number;
    row : number;
    colour : string;
    label : string;
    title : string;
    late : boolean;
    moved : boolean;
    ticks : { left : number; kind : string; title : string }[];
}
interface ILane {
    key : string;
    label : string;
    top : number;
    height : number;
    rows : number;
    bars : IBar[];
    down : { left : number; width : number; title : string; emergency : boolean }[];
    // The combination strip along the top of the lane, and how far down the bars start below it
    configs : { left : number; width : number; label : string; title : string }[];
    barTop : number;
}
interface ITick {
    left : number;
    label : string;
    major : boolean;
}
// Where a dragged bar is going
interface IDrag {
    assignment : IAssignment;
    startX : number;
    startY : number;
    startLeft : number;
    left : number;
    top : number;
    width : number;
    laneKey : string;
    moved : boolean;
}
// A bar dropped somewhere else, a what-if move
export interface IGanttMove {
    itemKey : string;
    resourceKey : string;
    start : string;
}

const LABEL_WIDTH = 168;
const BAR_HEIGHT = 22;
const LANE_PAD = 6;
// Tall enough for the resource's name under its key
const MIN_LANE = 44;
const HEADER = 40;
// The strip showing which combination a resource is in
const CONFIG_HEIGHT = 16;
// Twelve colours that stay apart in both themes
const PALETTE = ['#4e79a7', '#f28e2b', '#59a14f', '#b07aa1', '#e15759', '#76b7b2', '#edc948', '#9c755f', '#ff9da7', '#6b8e23', '#8e6bbf', '#d37295'];

// Parses the API's local times, they have no zone
export function toDate(text : string) : Date {
    return new Date(text.length == 19 ? text : text.substring(0, 19));
}
// A local time as the API takes it, yyyy-MM-ddTHH:mm:ss
export function toLocalText(date : Date) : string {
    const pad = (x : number) => String(x).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:00`;
}
export function formatTime(text : string | null | undefined) : string {
    return text ? text.substring(0, 16).replace('T', ' ') : '';
}
// The same value always gets the same colour
export function colourFor(value : string) : string {
    let hash = 0;
    for(let i = 0; i < value.length; i++)
        hash = (hash * 31 + value.charCodeAt(i)) | 0;
    return PALETTE[Math.abs(hash) % PALETTE.length];
}

// The plan by resource, one lane each, bars stack when a resource runs several items at once, drag a bar to try it somewhere else
@Component({
    selector : 'app-gantt-chart',
    templateUrl : './ganttChart.html',
    styleUrl : './ganttChart.scss',
})
export class GanttChart {
    readonly details = input.required<IRunDetails>();
    readonly assignments = input.required<IAssignment[]>();
    readonly colourBy = input<string | null>(null);
    readonly labelBy = input<string | null>(null);
    readonly hourWidth = input<number>(6);
    readonly selectedKey = input<string | null>(null);
    // Items with a what-if move waiting, shown dashed
    readonly movedKeys = input<string[]>([]);
    readonly lockedUntil = input<string | null>(null);
    readonly editable = input<boolean>(true);
    readonly selected = output<string | null>();
    readonly moved = output<IGanttMove>();

    private readonly scroller = viewChild<ElementRef<HTMLDivElement>>('scroller');
    readonly drag = signal<IDrag | null>(null);
    readonly labelWidth = LABEL_WIDTH;
    readonly header = HEADER;

    readonly start = computed<number>(() => toDate(this.details().horizonStart).getTime());
    readonly end = computed<number>(() => toDate(this.details().horizonEnd).getTime());
    readonly width = computed<number>(() => Math.max(200, (this.end() - this.start()) / 3600000 * this.hourWidth()));

    // Each value gets its own colour from the palette, in order, so neighbours don't clash the way a hash can
    readonly colours = computed<Map<string, string>>(() => {
        const colourBy = this.colourBy();
        const values = new Set<string>();
        for(const assignment of this.assignments())
            values.add(colourBy ? String(assignment.attributes?.[colourBy] ?? '(none)') : assignment.itemKey);
        const map = new Map<string, string>();
        [...values].sort().forEach((x, i) => map.set(x, PALETTE[i % PALETTE.length]));
        return map;
    });
    readonly lanes = computed<ILane[]>(() => {
        const details = this.details();
        const colours = this.colours();
        const byResource = new Map<string, IAssignment[]>();
        for(const assignment of this.assignments()) {
            const list = byResource.get(assignment.resourceKey) ?? [];
            list.push(assignment);
            byResource.set(assignment.resourceKey, list);
        }
        const moved = new Set(this.movedKeys());
        const emergency = details.downs ?? [];
        const lanes : ILane[] = [];
        let top = 0;
        const keys = details.resources.map(x => x.key);
        for(const key of byResource.keys())
            if(!keys.includes(key))
                keys.push(key);
        for(const key of keys) {
            const resource = details.resources.find(x => x.key == key);
            const list = (byResource.get(key) ?? []).slice().sort((a, b) => a.startTime.localeCompare(b.startTime));
            // each bar goes on the first sub row that's free by its start
            const rowEnds : number[] = [];
            const bars : IBar[] = [];
            for(const assignment of list) {
                const start = toDate(assignment.startTime).getTime();
                const end = toDate(assignment.endTime).getTime();
                let row = rowEnds.findIndex(x => x <= start);
                if(row < 0) {
                    row = rowEnds.length;
                    rowEnds.push(end);
                }
                else
                    rowEnds[row] = end;
                bars.push(this.makeBar(assignment, start, end, row, moved.has(assignment.itemKey), colours));
            }
            const rows = Math.max(1, rowEnds.length);
            const down = (resource?.unavailable ?? []).map(x => this.makeDown(x, false));
            for(const item of emergency.filter(x => x.resourceKey == key))
                down.push(this.makeDown({ start : item.from, end : item.to, reason : item.reason ?? 'Down' }, true));
            const configs = (resource?.configurations ?? []).map(x => this.makeConfig(x));
            const barTop = configs.length ? LANE_PAD + CONFIG_HEIGHT : LANE_PAD;
            const height = Math.max(MIN_LANE, rows * (BAR_HEIGHT + 2) + LANE_PAD + barTop);
            lanes.push({ key : key, label : resource?.label ?? key, top : top, height : height, rows : rows, bars : bars, down : down, configs : configs, barTop : barTop });
            top += height;
        }
        return lanes;
    });
    readonly totalHeight = computed<number>(() => this.lanes().reduce((sum, x) => sum + x.height, 0));

    // Day labels, and hour lines when there's room
    readonly ticks = computed<ITick[]>(() => {
        const ticks : ITick[] = [];
        const hourWidth = this.hourWidth();
        const step = hourWidth >= 40 ? 1 : hourWidth >= 16 ? 3 : hourWidth >= 8 ? 6 : 24;
        const first = new Date(this.start());
        first.setMinutes(0, 0, 0);
        for(let time = first.getTime(); time <= this.end(); time += 3600000) {
            const date = new Date(time);
            const hour = date.getHours();
            if(hour % step != 0 || time < this.start())
                continue;
            const major = hour == 0;
            const label = major ? date.toLocaleDateString(undefined, { weekday : 'short', day : 'numeric', month : 'short' }) : step < 24 ? `${String(hour).padStart(2, '0')}` : '';
            ticks.push({ left : this.toX(time), label : label, major : major });
        }
        return ticks;
    });
    readonly nowX = computed<number | null>(() => {
        const now = Date.now();
        return now >= this.start() && now <= this.end() ? this.toX(now) : null;
    });
    readonly lockX = computed<number | null>(() => {
        const until = this.lockedUntil();
        if(!until)
            return null;
        const time = toDate(until).getTime();
        return time > this.start() ? this.toX(Math.min(time, this.end())) : null;
    });
    readonly legend = computed<{ value : string; colour : string }[]>(() => {
        const colourBy = this.colourBy();
        if(!colourBy)
            return [];
        const values = new Set<string>();
        for(const assignment of this.assignments())
            values.add(String(assignment.attributes?.[colourBy] ?? '(none)'));
        const colours = this.colours();
        return [...values].sort().slice(0, 24).map(x => ({ value : x, colour : colours.get(x) ?? colourFor(x) }));
    });

    toX(time : number) : number {
        return (time - this.start()) / 3600000 * this.hourWidth();
    }

    onBarDown(event : PointerEvent, bar : IBar, lane : ILane) : void {
        event.stopPropagation();
        this.selected.emit(bar.assignment.itemKey);
        if(!this.editable() || bar.assignment.fixed || event.button != 0)
            return;
        (event.target as HTMLElement).setPointerCapture(event.pointerId);
        this.drag.set({
            assignment : bar.assignment,
            startX : event.clientX,
            startY : event.clientY,
            startLeft : bar.left,
            left : bar.left,
            top : lane.top + LANE_PAD,
            width : bar.width,
            laneKey : lane.key,
            moved : false
        });
    }
    onBarMove(event : PointerEvent) : void {
        const drag = this.drag();
        if(!drag)
            return;
        const dx = event.clientX - drag.startX;
        const dy = event.clientY - drag.startY;
        if(!drag.moved && Math.abs(dx) < 4 && Math.abs(dy) < 4)
            return;
        const lane = this.laneAt(event.clientY);
        this.drag.set({ ...drag, moved : true, left : Math.max(0, drag.startLeft + dx), laneKey : lane?.key ?? drag.laneKey, top : (lane?.top ?? drag.top) + LANE_PAD });
    }
    onBarUp(event : PointerEvent) : void {
        const drag = this.drag();
        this.drag.set(null);
        if(!drag?.moved)
            return;
        // snaps to the run's time buckets
        const bucket = this.details().bucketMinutes * 60000;
        const time = Math.round((this.start() + drag.left / this.hourWidth() * 3600000) / bucket) * bucket;
        this.moved.emit({ itemKey : drag.assignment.itemKey, resourceKey : drag.laneKey, start : toLocalText(new Date(time)) });
    }
    onBackgroundClick() : void {
        this.selected.emit(null);
    }

    private laneAt(clientY : number) : ILane | undefined {
        const element = this.scroller()?.nativeElement;
        if(!element)
            return undefined;
        const y = clientY - element.getBoundingClientRect().top + element.scrollTop - HEADER;
        return this.lanes().find(x => y >= x.top && y < x.top + x.height);
    }
    private makeBar(assignment : IAssignment, start : number, end : number, row : number, moved : boolean, colours : Map<string, string>) : IBar {
        const colourBy = this.colourBy();
        const labelBy = this.labelBy();
        const colourValue = colourBy ? String(assignment.attributes?.[colourBy] ?? '(none)') : assignment.itemKey;
        const label = (labelBy ? assignment.attributes?.[labelBy] : null) ?? assignment.itemLabel ?? assignment.itemKey;
        const lines = [
            `${label}${assignment.segment > 0 ? ` (part ${assignment.segment + 1})` : ''}`,
            `${formatTime(assignment.startTime)} to ${formatTime(assignment.endTime)}`,
            assignment.poolKey ? `${assignment.toolCount} x ${assignment.poolKey}, ${assignment.cycles} cycles, ${assignment.quantity} made` : `${assignment.quantity} made`,
            assignment.setupMinutes ? `Setup ${assignment.setupMinutes} min` : '',
            assignment.dueTime ? `Due ${formatTime(assignment.dueTime)}${assignment.lateMinutes > 0 ? `, ${Math.round(assignment.lateMinutes / 60)} h late` : ''}` : '',
            ...(assignment.costReasons ?? []),
            assignment.fixed ? 'Locked' : ''
        ];
        const ticks = (assignment.inspections ?? []).map(x => ({ left : this.toX(toDate(x.at).getTime()) - this.toX(start), kind : x.kind, title : `${x.kind} inspection ${formatTime(x.at)}, ${x.minutes} min` }));
        return {
            assignment : assignment,
            left : this.toX(start),
            width : Math.max(2, this.toX(end) - this.toX(start)),
            row : row,
            colour : colours.get(colourValue) ?? colourFor(colourValue),
            label : String(label),
            title : lines.filter(x => x).join('\n'),
            late : assignment.lateMinutes > 0,
            moved : moved,
            ticks : ticks
        };
    }
    private makeConfig(range : IConfigurationRange) : { left : number; width : number; label : string; title : string } {
        const start = Math.max(this.start(), toDate(range.start).getTime());
        const end = Math.min(this.end(), toDate(range.end).getTime());
        const title = `${range.label}${range.ruleName ? ' (' + range.ruleName + ')' : ''}: ${formatTime(range.start)} to ${formatTime(range.end)}`;
        return { left : this.toX(start), width : Math.max(0, this.toX(end) - this.toX(start)), label : range.label, title : title };
    }
    private makeDown(range : IDownRange, emergency : boolean) : { left : number; width : number; title : string; emergency : boolean } {
        const start = Math.max(this.start(), toDate(range.start).getTime());
        const end = Math.min(this.end(), toDate(range.end).getTime());
        return { left : this.toX(start), width : Math.max(0, this.toX(end) - this.toX(start)), title : `${range.reason ?? 'Down'}: ${formatTime(range.start)} to ${formatTime(range.end)}`, emergency : emergency };
    }
}
