import { Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IUserRole, SchedulerApi, UserRole } from '../../api/schedulerApi';
import { WorkspaceService } from '../../workspace/workspaceService';

// A role a profile's users can be given, with what it lets them do
interface IRoleChoice {
    role : UserRole;
    label : string;
    description : string;
}
export const PROJECT_ROLES : IRoleChoice[] = [
    { role : 'ProjectOwner', label : 'Project owner', description : 'Changes everything in the profile and says who else can use it' },
    { role : 'ProjectReader', label : 'Project reader', description : 'Sees the whole profile and its schedule, changes nothing' },
    { role : 'ProjectSolver', label : 'Project solver', description : 'Only the schedule: solve, publish, locks and solve settings' }
];

// Users & roles: the site owners, for a site owner, and the open profile's owners, readers and solvers, for its owners
// users are Windows domain users or groups, e.g. CONTOSO\jsmith or CONTOSO\Planners
@Component({
    selector : 'app-users-doc',
    imports : [FormsModule],
    templateUrl : './usersDoc.html',
    styleUrl : './usersDoc.scss',
})
export class UsersDoc {
    private readonly api = inject(SchedulerApi);
    readonly workspace = inject(WorkspaceService);
    readonly roles = PROJECT_ROLES;

    readonly siteOwners = signal<IUserRole[]>([]);
    readonly profileUsers = signal<IUserRole[]>([]);
    readonly busy = signal<boolean>(false);
    newSiteOwner : string = '';
    newUser : string = '';
    newRole : UserRole = 'ProjectReader';

    constructor() {
        // read again when another profile is opened
        effect(() => {
            this.workspace.profileId();
            this.load();
        });
    }
    async load() : Promise<void> {
        const profileId = this.workspace.profileId();
        try {
            this.siteOwners.set(this.workspace.user()?.siteOwner ? await this.api.getSiteOwners() : []);
            this.profileUsers.set(profileId && this.workspace.rights().manageUsers ? await this.api.getProfileUsers(profileId) : []);
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
    }
    getRoleLabel(role : UserRole) : string {
        return this.roles.find(x => x.role == role)?.label ?? 'Site owner';
    }
    getRoleTone(role : UserRole) : string {
        return role == 'ProjectOwner' ? 'super' : role == 'ProjectSolver' ? 'warn' : 'accent';
    }
    formatDate(date : string) : string {
        return date ? date.substring(0, 10) : '';
    }

    async addSiteOwner() : Promise<void> {
        await this.change(async () => this.siteOwners.set(await this.api.addSiteOwner(this.newSiteOwner)), () => this.newSiteOwner = '');
    }
    async removeSiteOwner(role : IUserRole) : Promise<void> {
        await this.change(async () => this.siteOwners.set(await this.api.deleteSiteOwner(role.id)));
    }
    async addUser() : Promise<void> {
        const profileId = this.workspace.profileId();
        if(profileId)
            await this.change(async () => this.profileUsers.set(await this.api.addProfileUser(profileId, this.newUser, this.newRole)), () => this.newUser = '');
    }
    async removeUser(role : IUserRole) : Promise<void> {
        const profileId = this.workspace.profileId();
        if(profileId)
            await this.change(async () => this.profileUsers.set(await this.api.deleteProfileUser(profileId, role.id)));
    }
    // Makes a change, then logs in again quietly so the user's own rights are up to date
    private async change(action : () => Promise<void>, done? : () => void) : Promise<void> {
        this.busy.set(true);
        try {
            await action();
            done?.();
            await this.workspace.refreshUser();
            this.workspace.toast('Roles saved', 'ok');
        }
        catch(e) {
            this.workspace.toast(getErrorMessage(e), 'error');
        }
        this.busy.set(false);
    }
}
