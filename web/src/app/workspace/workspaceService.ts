import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { getErrorMessage, INavNode, IProfile, IStatus, SchedulerApi } from '../api/schedulerApi';

export type DocKind = 'welcome' | 'profile' | 'openProfile' | 'connection' | 'sourceBrowser' | 'source' | 'object'
    | 'entity' | 'relationship' | 'calendar' | 'rule' | 'scales' | 'schedule' | 'solveSettings';

// A tab in the document area, params are what its kind needs, e.g. connectionId
export interface IDocTab {
    id : string;
    kind : DocKind;
    title : string;
    icon : string;
    params : {[name : string] : string | number | undefined};
}
export interface IToast {
    id : number;
    text : string;
    tone : 'info' | 'ok' | 'error';
}

export type Theme = 'light' | 'dark';
export type Density = 'comfortable' | 'compact';

// The view settings kept in the browser between visits
interface IPrefs {
    theme : Theme;
    density : Density;
    navigator : boolean;
    properties : boolean;
    ribbonCollapsed : boolean;
    profileId : string | null;
}
const PREFS_KEY = 'scheduler.prefs';

function loadPrefs() : IPrefs {
    const defaults : IPrefs = {
        theme : window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
        density : 'comfortable',
        navigator : true,
        properties : true,
        ribbonCollapsed : false,
        profileId : null
    };
    try {
        const saved = JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}');
        // ids were numbers before they were guids
        if(typeof saved.profileId != 'string')
            saved.profileId = null;
        return { ...defaults, ...saved };
    }
    catch(e) {
        return defaults;
    }
}

// Finds a node anywhere in the navigator tree
export function findNode(nodes : INavNode[], id : string) : INavNode | undefined {
    for(const node of nodes) {
        if(node.id == id)
            return node;
        const found = node.children ? findNode(node.children, id) : undefined;
        if(found)
            return found;
    }
    return undefined;
}

// Everything the screen shares, the open profile, the navigator, what's selected, the open documents and the view settings
@Injectable({ providedIn : 'root' })
export class WorkspaceService {
    private readonly api = inject(SchedulerApi);
    private readonly prefs = loadPrefs();
    private toastCount = 0;

    readonly theme = signal<Theme>(this.prefs.theme);
    readonly density = signal<Density>(this.prefs.density);
    readonly showNavigator = signal<boolean>(this.prefs.navigator);
    readonly showProperties = signal<boolean>(this.prefs.properties);
    readonly ribbonCollapsed = signal<boolean>(this.prefs.ribbonCollapsed);

    readonly profiles = signal<IProfile[]>([]);
    readonly profileId = signal<string | null>(this.prefs.profileId);
    readonly profile = computed<IProfile | null>(() => this.profiles().find(x => x.id == this.profileId()) ?? null);

    readonly navigator = signal<INavNode[]>([]);
    readonly navigatorLoading = signal<boolean>(false);
    readonly selection = signal<INavNode | null>(null);

    readonly docs = signal<IDocTab[]>([{ id : 'welcome', kind : 'welcome', title : 'Start', icon : 'home', params : {} }]);
    readonly activeDocId = signal<string>('welcome');
    readonly activeDoc = computed<IDocTab | null>(() => this.docs().find(x => x.id == this.activeDocId()) ?? null);

    readonly status = signal<IStatus | null>(null);
    readonly statusError = signal<string>('');
    readonly lastSolve = signal<string | null>(null);
    readonly toasts = signal<IToast[]>([]);

    // The connection selected in the navigator, or the one the open document is for
    readonly selectedConnectionId = computed<string | null>(() => {
        const selected = this.selection();
        if(selected?.kind == 'connection')
            return selected.itemId ?? null;
        return (this.activeDoc()?.params['connectionId'] as string | undefined) ?? null;
    });
    // The source selected in the navigator, or the open source document's
    readonly selectedSourceId = computed<string | null>(() => {
        const selected = this.selection();
        if(selected?.kind == 'source')
            return selected.itemId ?? null;
        const doc = this.activeDoc();
        return doc?.kind == 'source' ? doc.params['sourceId'] as string : null;
    });
    // Connections whose last test failed, until the rule checks arrive
    readonly validationCount = computed<number>(() => this.getFolder('connections').filter(x => x.badge == 'error').length);

    constructor() {
        // the theme and density go on the page, and every setting is kept for next time
        effect(() => {
            document.documentElement.dataset['theme'] = this.theme();
            document.documentElement.dataset['density'] = this.density();
            const prefs : IPrefs = {
                theme : this.theme(),
                density : this.density(),
                navigator : this.showNavigator(),
                properties : this.showProperties(),
                ribbonCollapsed : this.ribbonCollapsed(),
                profileId : this.profileId()
            };
            try {
                localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
            }
            catch(e) {
            }
        });
    }

    // Loads the status, the profiles and the last profile used, or the first one
    async init() : Promise<void> {
        await this.refreshStatus();
        try {
            const profiles = await this.api.getProfiles();
            this.profiles.set(profiles);
            const keep = profiles.find(x => x.id == this.profileId()) ?? profiles[0] ?? null;
            this.profileId.set(keep?.id ?? null);
            await this.refreshNavigator();
        }
        catch(e) {
            this.toast(getErrorMessage(e), 'error');
        }
    }
    async refreshStatus() : Promise<void> {
        try {
            this.status.set(await this.api.getStatus());
            this.statusError.set('');
        }
        catch(e) {
            this.status.set(null);
            this.statusError.set(getErrorMessage(e));
        }
    }
    async refreshProfiles() : Promise<void> {
        this.profiles.set(await this.api.getProfiles());
    }
    // Opening another profile closes its documents, connections are shared so theirs stay open
    async openProfile(profileId : string) : Promise<void> {
        this.profileId.set(profileId);
        this.selection.set(null);
        this.docs.update(x => x.filter(y => y.kind == 'welcome' || y.kind == 'connection'));
        this.activeDocId.set('welcome');
        await this.refreshNavigator();
    }
    async refreshNavigator() : Promise<void> {
        const profileId = this.profileId();
        if(profileId == null) {
            this.navigator.set([]);
            return;
        }
        this.navigatorLoading.set(true);
        try {
            const tree = await this.api.getNavigator(profileId);
            this.navigator.set(tree);
            // the selection points at the new node so the properties panel shows the new values
            const selected = this.selection();
            if(selected)
                this.selection.set(findNode(tree, selected.id) ?? null);
        }
        catch(e) {
            this.toast(getErrorMessage(e), 'error');
        }
        this.navigatorLoading.set(false);
    }
    // The items in one of the navigator's folders, e.g. connections
    getFolder(folderId : string) : INavNode[] {
        return this.navigator().find(x => x.id == folderId)?.children ?? [];
    }
    select(node : INavNode | null) : void {
        this.selection.set(node);
    }
    // Opens a document, or brings it to the front with the new params when it's already open
    openDoc(doc : IDocTab) : void {
        if(!this.docs().some(x => x.id == doc.id))
            this.docs.update(x => [...x, doc]);
        else
            this.docs.update(x => x.map(y => y.id == doc.id ? { ...y, ...doc } : y));
        this.activeDocId.set(doc.id);
    }
    closeDoc(docId : string) : void {
        const docs = this.docs();
        const index = docs.findIndex(x => x.id == docId);
        if(index < 0)
            return;
        const left = docs.filter(x => x.id != docId);
        this.docs.set(left);
        if(this.activeDocId() == docId)
            this.activeDocId.set(left[Math.max(0, index - 1)]?.id ?? '');
    }
    // Opens the document that goes with a navigator item
    openNode(node : INavNode) : void {
        if(node.kind == 'connection')
            this.openSourceBrowser(node.itemId!, node.label);
        else if(node.kind == 'source')
            this.openDoc({ id : node.id, kind : 'source', title : node.label, icon : 'table', params : { sourceId : node.itemId! } });
        else if(node.kind == 'entity')
            this.openDoc({ id : node.id, kind : 'entity', title : node.label, icon : node.icon, params : { entityTypeId : node.itemId! } });
        else if(node.kind == 'relationship')
            this.openDoc({ id : node.id, kind : 'relationship', title : node.label, icon : 'grid_on', params : { relationshipId : node.itemId! } });
        else if(node.kind == 'calendar')
            this.openDoc({ id : node.id, kind : 'calendar', title : node.label, icon : 'calendar_month', params : { calendarId : node.itemId! } });
        else if(node.kind == 'rule')
            this.openDoc({ id : node.id, kind : 'rule', title : node.label, icon : 'rule', params : { ruleId : node.itemId! } });
        else if(node.kind == 'run')
            this.openRun(node.itemId!);
        else if(node.kind != 'folder')
            this.openDoc({ id : node.id, kind : 'object', title : node.label, icon : node.icon, params : { nodeId : node.id } });
    }
    // The schedule document, on a run or on the published plan when there's no run id
    openRun(runId : string | null) : void {
        this.openDoc({ id : 'schedule', kind : 'schedule', title : 'Schedule', icon : 'view_timeline', params : { runId : runId ?? undefined, reload : Date.now() } });
    }
    // The run being looked at in the schedule document, for Stop and Publish
    readonly currentRunId = signal<string | null>(null);
    readonly solving = signal<boolean>(false);
    // The schedule's view, the Gantt or the table
    readonly scheduleView = signal<'gantt' | 'table'>('gantt');

    // The tables and views on a connection, reload makes an open one read them again
    openSourceBrowser(connectionId : string, title : string, reload : boolean = false) : void {
        this.openDoc({
            id : `browse:${connectionId}`,
            kind : 'sourceBrowser',
            title : title,
            icon : 'travel_explore',
            params : { connectionId : connectionId, reload : reload ? Date.now() : undefined }
        });
    }
    // A message in the bottom right that goes after a few seconds
    toast(text : string, tone : 'info' | 'ok' | 'error' = 'info') : void {
        const id = ++this.toastCount;
        this.toasts.update(x => [...x, { id : id, text : text, tone : tone }]);
        setTimeout(() => this.toasts.update(x => x.filter(y => y.id != id)), tone == 'error' ? 8000 : 4000);
    }
}
