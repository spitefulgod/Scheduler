import { Component, input } from '@angular/core';
import { IColumn } from '../../api/schedulerApi';

// A read only grid of rows with the header and row numbers kept in view
@Component({
    selector : 'app-data-grid',
    templateUrl : './dataGrid.html',
    styleUrl : './dataGrid.scss',
})
export class DataGrid {
    readonly columns = input.required<IColumn[]>();
    readonly rows = input.required<unknown[][]>();

    isNumber(value : unknown) : boolean {
        return typeof value == 'number';
    }
    getTitle(column : IColumn) : string {
        return column.sqlType + (column.nullable ? ', nullable' : '');
    }
    format(value : unknown) : string {
        if(value == null)
            return 'NULL';
        if(typeof value == 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(value))
            return value.replace('T', ' ').substring(0, 19);
        return String(value);
    }
}
