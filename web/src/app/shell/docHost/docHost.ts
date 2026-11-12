import { Component, inject } from '@angular/core';
import { ConnectionWizard } from '../../docs/connectionWizard/connectionWizard';
import { ObjectDoc } from '../../docs/objectDoc/objectDoc';
import { ScheduleDoc } from '../../docs/scheduleDoc/scheduleDoc';
import { EntityEditor } from '../../docs/entityEditor/entityEditor';
import { RelationshipEditor } from '../../docs/relationshipEditor/relationshipEditor';
import { CalendarEditor } from '../../docs/calendarEditor/calendarEditor';
import { RuleEditor } from '../../docs/ruleEditor/ruleEditor';
import { ValueScales } from '../../docs/valueScales/valueScales';
import { SolveSettings } from '../../docs/solveSettings/solveSettings';
import { OpenProfile } from '../../docs/openProfile/openProfile';
import { ProfileEditor } from '../../docs/profileEditor/profileEditor';
import { SourceBrowser } from '../../docs/sourceBrowser/sourceBrowser';
import { SourceDoc } from '../../docs/sourceDoc/sourceDoc';
import { UsersDoc } from '../../docs/usersDoc/usersDoc';
import { WelcomePage } from '../../docs/welcomePage/welcomePage';
import { IDocTab, WorkspaceService } from '../../workspace/workspaceService';

// The tabbed document area in the middle
@Component({
    selector : 'app-doc-host',
    imports : [WelcomePage, ProfileEditor, OpenProfile, ConnectionWizard, SourceBrowser, SourceDoc, ObjectDoc, ScheduleDoc,
        EntityEditor, RelationshipEditor, CalendarEditor, RuleEditor, ValueScales, SolveSettings, UsersDoc],
    templateUrl : './docHost.html',
    styleUrl : './docHost.scss',
})
export class DocHost {
    readonly workspace = inject(WorkspaceService);

    // Readers see the model but can't change it, connections are for site and project owners, new profiles for site owners
    isReadOnly(doc : IDocTab) : boolean {
        const user = this.workspace.user();
        switch(doc.kind) {
            case 'connection':
            case 'sourceBrowser':
                return !user?.designer;
            case 'profile':
                return doc.params['profileId'] ? !this.workspace.rights().editModel : !user?.siteOwner;
            case 'source':
            case 'entity':
            case 'relationship':
            case 'calendar':
            case 'rule':
            case 'scales':
            case 'object':
                return !this.workspace.rights().editModel;
            default:
                return false;
        }
    }
    close(event : Event, docId : string) : void {
        event.stopPropagation();
        this.workspace.closeDoc(docId);
    }
}
