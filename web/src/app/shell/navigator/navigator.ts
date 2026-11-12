import { NgTemplateOutlet } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { INavNode } from '../../api/schedulerApi';
import { WorkspaceService } from '../../workspace/workspaceService';

// The tree on the left, connections, sources, entities, relationships, calendars, rules and runs
@Component({
    selector : 'app-navigator',
    imports : [NgTemplateOutlet],
    templateUrl : './navigator.html',
    styleUrl : './navigator.scss',
})
export class Navigator {
    readonly workspace = inject(WorkspaceService);
    readonly filter = signal<string>('');
    // Folders and items closed by the user, runs start closed
    readonly collapsed = signal<string[]>(['runs']);

    // Everything is open while filtering so the matches show
    isOpen(node : INavNode) : boolean {
        return this.filter() != '' || !this.collapsed().includes(node.id);
    }
    toggle(node : INavNode, event? : Event) : void {
        event?.stopPropagation();
        this.collapsed.update(x => x.includes(node.id) ? x.filter(y => y != node.id) : [...x, node.id]);
    }
    // The nodes that match the filter, or have a child that does
    getVisible(nodes : INavNode[] | null | undefined) : INavNode[] {
        const filter = this.filter().trim().toLowerCase();
        if(!nodes)
            return [];
        if(!filter)
            return nodes;
        return nodes.filter(x => x.label.toLowerCase().includes(filter) || this.getVisible(x.children).length > 0);
    }
    getBadgeTone(node : INavNode) : string {
        return node.badge == 'ok' ? 'ok' : node.badge == 'error' ? 'error' : '';
    }
    getBadgeText(node : INavNode) : string {
        return node.badge == 'ok' ? '✓' : node.badge == 'error' ? '!' : node.badge ?? '';
    }
    onKey(event : KeyboardEvent, node : INavNode) : void {
        if(event.key == 'Enter')
            this.workspace.openNode(node);
        if(event.key == ' ' && node.children?.length) {
            event.preventDefault();
            this.toggle(node);
        }
    }
    onFilter(event : Event) : void {
        this.filter.set((event.target as HTMLInputElement).value);
    }
}
