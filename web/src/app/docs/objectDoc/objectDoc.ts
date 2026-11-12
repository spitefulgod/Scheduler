import { TitleCasePipe } from '@angular/common';
import { Component, computed, inject, input } from '@angular/core';
import { INavNode } from '../../api/schedulerApi';
import { findNode, IDocTab, WorkspaceService } from '../../workspace/workspaceService';

// The milestone each kind of item gets its own editor in
const EDITOR_MILESTONE : {[kind : string] : number} = {
    entity : 3,
    attribute : 3,
    relationship : 4,
    calendar : 4,
    rule : 5,
    run : 6
};

// Shows an item from the navigator until its own editor is built
@Component({
    selector : 'app-object-doc',
    imports : [TitleCasePipe],
    templateUrl : './objectDoc.html',
    styleUrl : './objectDoc.scss',
})
export class ObjectDoc {
    readonly doc = input.required<IDocTab>();
    private readonly workspace = inject(WorkspaceService);
    readonly node = computed<INavNode | undefined>(() => findNode(this.workspace.navigator(), this.doc().params['nodeId'] as string));
    readonly rows = computed<[string, unknown][]>(() => Object.entries(this.node()?.properties ?? {}));
    readonly milestone = computed<number | null>(() => EDITOR_MILESTONE[this.node()?.kind ?? ''] ?? null);

    getValues(node : INavNode) : unknown[] {
        return Object.values(node.properties ?? {});
    }
}
