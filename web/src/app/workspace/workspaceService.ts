import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { ApiError, getErrorMessage, ILogin, INavNode, IProfile, IProfileRights, IStatus, IUserSettings, SchedulerApi } from '../api/schedulerApi';

export type DocKind = 'welcome' | 'profile' | 'openProfile' | 'connection' | 'sourceBrowser' | 'source' | 'object'
    | 'entity' | 'relationship' | 'calendar' | 'rule' | 'scales' | 'schedule' | 'solveSettings' | 'users';

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

export type Density = 'comfortable' | 'compact';

// Why the login didn't work, for the login error page
export interface ILoginError {
    kind : 'network' | 'access' | 'other';
    title : string;
    message : string;
}
// What a user with no role on the open profile can do, nothing
const NO_RIGHTS : IProfileRights = { profileId : '', readModel : false, editModel : false, viewSchedule : false, solve : false, manageUsers : false };

// The view settings kept in the browser between visits, the sidebar and properties panel are kept per user on the server
interface IPrefs {
    density : Density;
    navigator : boolean;
    properties : boolean;
    ribbonCollapsed : boolean;
    profileId : string | null;
}
const PREFS_KEY = 'scheduler.prefs';

function loadPrefs() : IPrefs {
    const defaults : IPrefs = {
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
        // there's one look now, the old theme setting is dropped
        delete saved.theme;
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

    // The signed in Windows user, null until the login has worked
    readonly user = signal<ILogin | null>(null);
    readonly loginError = signal<ILoginError | null>(null);
    readonly loggingIn = signal<boolean>(false);
    readonly loggedIn = computed<boolean>(() => this.user() != null);
    // Kept per user on the server, the navigation sidebar shrunk to its icons and the properties panel shrunk to a strip
    readonly sidebarCollapsed = signal<boolean>(false);
    readonly propertiesCollapsed = signal<boolean>(false);
    readonly density = signal<Density>(this.prefs.density);
    readonly showNavigator = signal<boolean>(this.prefs.navigator);
    readonly showProperties = signal<boolean>(this.prefs.properties);
    readonly ribbonCollapsed = signal<boolean>(this.prefs.ribbonCollapsed);

    readonly profiles = signal<IProfile[]>([]);
    readonly profileId = signal<string | null>(this.prefs.profileId);
    readonly profile = computed<IProfile | null>(() => this.profiles().find(x => x.id == this.profileId()) ?? null);
    // What the user can do in the open profile, a site owner can do everything, the API checks every call again
    readonly rights = computed<IProfileRights>(() => {
        const user = this.user();
        const profileId = this.profileId();
        if(!user || !profileId)
            return NO_RIGHTS;
        if(user.siteOwner)
            return { profileId : profileId, readModel : true, editModel : true, viewSchedule : true, solve : true, manageUsers : true };
        return user.profiles.find(x => x.profileId == profileId) ?? NO_RIGHTS;
    });
    // A solver only, they see the schedule and nothing of the model behind it
    readonly solverOnly = computed<boolean>(() => this.rights().viewSchedule && !this.rights().readModel);
    // Can give out roles somewhere, the site's owners or any profile's users
    readonly canManageUsers = computed<boolean>(() => this.user()?.siteOwner == true || this.user()?.profiles.some(x => x.manageUsers) == true);

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
        // the density goes on the page, and every setting is kept for next time
        effect(() => {
            document.documentElement.dataset['density'] = this.density();
            const prefs : IPrefs = {
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

    // Logs in with the Windows sign in, then loads the status, the profiles and the last profile used, or the first one
    // nothing else is asked of the API until the login works, the login error page has Try again
    async init() : Promise<void> {
        if(!await this.login())
            return;
        await this.refreshStatus();
        try {
            const profiles = await this.api.getProfiles();
            this.profiles.set(profiles);
            const keep = profiles.find(x => x.id == this.profileId()) ?? profiles[0] ?? null;
            this.profileId.set(keep?.id ?? null);
            await this.refreshNavigator();
            this.openStartDoc();
        }
        catch(e) {
            this.toast(getErrorMessage(e), 'error');
        }
    }
    async login() : Promise<boolean> {
        this.loggingIn.set(true);
        try {
            const user = await this.api.login();
            this.user.set(user);
            this.sidebarCollapsed.set(user.settings?.sidebarCollapsed == true);
            this.propertiesCollapsed.set(user.settings?.propertiesCollapsed == true);
            this.loginError.set(null);
        }
        catch(e) {
            this.user.set(null);
            this.loginError.set(this.getLoginError(e));
        }
        this.loggingIn.set(false);
        return this.user() != null;
    }
    // Reads the user's rights again, e.g. after their roles changed
    async refreshUser() : Promise<void> {
        try {
            this.user.set(await this.api.login());
            await this.refreshProfiles();
        }
        catch(e) {
            this.toast(getErrorMessage(e), 'error');
        }
    }
    private getLoginError(e : unknown) : ILoginError {
        const status = e instanceof ApiError ? e.status : -1;
        if(status == 0)
            return { kind : 'network', title : 'Unable to connect', message : getErrorMessage(e) };
        if(status == 401 || status == 403)
            return { kind : 'access', title : 'Access denied', message : 'You do not have access to the Scheduler, sign in to Windows with your domain account' };
        return { kind : 'other', title : 'Unable to log in', message : getErrorMessage(e) };
    }
    // The sidebar and the properties panel are remembered for the user, wherever they sign in
    toggleSidebar() : void {
        this.sidebarCollapsed.update(x => !x);
        this.saveSettings({ sidebarCollapsed : this.sidebarCollapsed() });
    }
    toggleProperties() : void {
        this.propertiesCollapsed.update(x => !x);
        this.saveSettings({ propertiesCollapsed : this.propertiesCollapsed() });
    }
    private async saveSettings(settings : IUserSettings) : Promise<void> {
        try {
            await this.api.saveUserSettings(settings);
        }
        catch(e) {
            this.toast(`Your settings weren't saved: ${getErrorMessage(e)}`, 'error');
        }
    }
    // A solver goes straight to the schedule, it's all they can see
    private openStartDoc() : void {
        if(this.solverOnly())
            this.openRun(null);
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
        this.openStartDoc();
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
