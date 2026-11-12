import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getErrorMessage, IConnectionDetails, ITestResult, SchedulerApi } from '../../api/schedulerApi';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';

// Adds or changes a connection, provider, then server, database and sign in, then test and save
// a password is only ever sent to the API, it never comes back
@Component({
    selector : 'app-connection-wizard',
    imports : [FormsModule],
    templateUrl : './connectionWizard.html',
    styleUrl : './connectionWizard.scss',
})
export class ConnectionWizard implements OnInit {
    readonly doc = input.required<IDocTab>();
    private readonly api = inject(SchedulerApi);
    private readonly workspace = inject(WorkspaceService);

    readonly step = signal<1 | 2 | 3>(1);
    readonly busy = signal<boolean>(false);
    readonly error = signal<string>('');
    readonly test = signal<ITestResult | null>(null);
    readonly hasSavedPassword = signal<boolean>(false);
    readonly connectionId = signal<string | null>(null);
    readonly providers = [
        { id : 'SQLSERVER', label : 'Microsoft SQL Server', icon : 'database', available : true },
        { id : 'POSTGRESQL', label : 'PostgreSQL', icon : 'database', available : false },
        { id : 'ORACLE', label : 'Oracle', icon : 'database', available : false },
        { id : 'ODBC', label : 'ODBC', icon : 'cable', available : false }
    ];

    name : string = '';
    server : string = '';
    database : string = '';
    // Windows signs in as whoever the API runs as, a SQL Server login needs a user and password
    signIn : 'windows' | 'login' = 'windows';
    user : string = '';
    password : string = '';
    trustServerCertificate : boolean = true;

    async ngOnInit() : Promise<void> {
        const connectionId = this.doc().params['connectionId'] as string | undefined;
        if(!connectionId)
            return;
        try {
            const connection = await this.api.getConnection(connectionId);
            this.connectionId.set(connectionId);
            this.hasSavedPassword.set(connection.hasPassword);
            this.name = connection.name;
            this.server = connection.settings.server ?? '';
            this.database = connection.settings.database ?? '';
            this.user = connection.settings.user ?? '';
            this.signIn = this.user ? 'login' : 'windows';
            this.trustServerCertificate = connection.settings.trustServerCertificate ?? true;
            this.step.set(2);
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
    }
    canContinue() : boolean {
        return !!this.name.trim() && !!this.server.trim() && !!this.database.trim() && (this.signIn == 'windows' || !!this.user.trim());
    }
    onChange() : void {
        this.test.set(null);
    }
    async runTest() : Promise<void> {
        this.busy.set(true);
        this.error.set('');
        try {
            // a blank password on a saved connection tests with the one it has
            this.test.set(await this.api.testDetails(this.connectionId(), this.getDetails()));
            this.step.set(3);
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
        this.busy.set(false);
    }
    async save() : Promise<void> {
        this.busy.set(true);
        this.error.set('');
        try {
            const saved = await this.api.saveConnection(this.connectionId(), this.getDetails());
            await this.api.testConnection(saved.id).catch(() => undefined);
            await this.workspace.refreshNavigator();
            this.workspace.toast(`Connection ${saved.name} saved`, 'ok');
            this.workspace.closeDoc(this.doc().id);
            this.workspace.openSourceBrowser(saved.id, saved.name, true);
        }
        catch(e) {
            this.error.set(getErrorMessage(e));
        }
        this.busy.set(false);
    }
    getSignInText() : string {
        return this.signIn == 'windows' ? 'Windows' : this.user;
    }

    private getDetails() : IConnectionDetails {
        const login = this.signIn == 'login';
        return {
            name : this.name.trim(),
            typeId : 'SQLSERVER',
            settings : { server : this.server.trim(), database : this.database.trim(), user : login ? this.user.trim() : '', trustServerCertificate : this.trustServerCertificate },
            password : login && this.password ? this.password : null
        };
    }
}
