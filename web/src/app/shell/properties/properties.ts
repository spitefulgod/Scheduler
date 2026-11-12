import { Component, computed, inject } from '@angular/core';
import { WorkspaceService } from '../../workspace/workspaceService';

// The panel on the right, what's known about the item selected in the navigator, or the profile when nothing is
@Component({
    selector : 'app-properties',
    templateUrl : './properties.html',
    styleUrl : './properties.scss',
})
export class Properties {
    readonly workspace = inject(WorkspaceService);
    readonly rows = computed<[string, unknown][]>(() => Object.entries(this.workspace.selection()?.properties ?? {}));

    formatDate(date : string | null) : string {
        return date ? date.substring(0, 16).replace('T', ' ') : '';
    }
}
