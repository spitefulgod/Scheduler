import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';

export interface IProfile {
    id : string;
    name : string;
    description : string | null;
    // The size of the solver's time buckets
    timeUnitMinutes : number;
    settings? : IProfileSettings | null;
    created : string;
    modified : string | null;
}

// What SQL Server needs to connect, a blank user signs in with Windows
export interface ISqlServerSettings {
    server : string;
    database : string;
    user : string;
    trustServerCertificate : boolean;
}
// A read only connection to where the data comes from, never with its password, only whether it has one
export interface IConnection {
    id : string;
    name : string;
    // SQLSERVER, or DEMO for the made up data in the scheduler's own database
    typeId : 'SQLSERVER' | 'DEMO';
    typeName : string;
    settings : ISqlServerSettings;
    hasPassword : boolean;
    lastTested : string | null;
    lastTestOk : boolean | null;
    lastTestMessage : string | null;
}
// What's sent to add, change or test one, a blank password keeps the one it has
export interface IConnectionDetails {
    name : string;
    typeId : 'SQLSERVER';
    settings : ISqlServerSettings;
    password : string | null;
}
export interface ITestResult {
    ok : boolean;
    message : string;
}

export type SourceKind = 'table' | 'view' | 'query';

// A table or view on a connection
export interface ISourceObject {
    schema : string;
    name : string;
    type : 'table' | 'view';
}
export interface IColumn {
    name : string;
    sqlType : string;
    nullable : boolean;
}
// The first rows of a table, view or query, truncated when there are more
export interface IPreview {
    columns : IColumn[];
    rows : unknown[][];
    truncated : boolean;
}
// A table, view or saved select a profile reads from, with its columns as last read
export interface ISource {
    id : string;
    profileId : string;
    connectionId : string;
    connectionName : string;
    name : string;
    kind : SourceKind;
    schema : string | null;
    objectName : string | null;
    queryText : string | null;
    columns : IColumn[];
    columnsRead : string | null;
}
export interface ISourceDetails {
    connectionId : string;
    name : string;
    kind : SourceKind;
    schema : string | null;
    objectName : string | null;
    queryText : string | null;
}

// One item in the navigator, folders have children, itemId is the id of the connection, source, etc.
export interface INavNode {
    id : string;
    kind : string;
    label : string;
    icon : string;
    itemId? : string | null;
    badge? : string | null;
    properties? : {[name : string] : string | number | boolean | null} | null;
    children? : INavNode[] | null;
}

// Which server and database the API is using
export interface IStatus {
    server : string;
    database : string;
    schemaVersion : number | null;
}
export interface IRuleType {
    id : string;
    name : string;
    category : string;
    description : string | null;
    compilesTo : string | null;
}

// A rule block's parameter, the rule builder draws its editor from this
export interface IRuleParameter {
    name : string;
    label : string;
    // entity and relationship pick from the profile, expression and condition are formulas, select picks from options
    type : 'entity' | 'relationship' | 'expression' | 'condition' | 'number' | 'boolean' | 'select';
    // entity: the kind it must be, e.g. Resource
    kind? : string | null;
    // what a formula can see: item, resource, tooling or mode (all three)
    scope? : 'item' | 'resource' | 'tooling' | 'mode' | null;
    options? : string[] | null;
    default? : string | null;
    required? : boolean | null;
    help? : string | null;
}
export interface IRuleTypeDetails extends IRuleType {
    supportsHard : boolean;
    supportsSoft : boolean;
    parameters : IRuleParameter[];
}

export type EntityKind = 'Item' | 'Resource' | 'Reference';
export interface IAttribute {
    id : string | null;
    name : string;
    dataType : string;
    role : string;
    sourceColumn : string | null;
    referencesEntityTypeId : string | null;
    referencesEntity? : string | null;
    sortOrder : number;
}
export interface IEntity {
    id : string;
    profileId : string;
    name : string;
    kind : EntityKind;
    description : string | null;
    sourceId : string | null;
    sourceName : string | null;
    durationFormula : string | null;
    attributes : IAttribute[];
}
export interface IEntityDetails {
    name : string;
    kind : EntityKind;
    description : string | null;
    sourceId : string | null;
    durationFormula : string | null;
}
// The entity's rows as the rule engine sees them, one value per attribute
export interface IEntityRows {
    attributes : string[];
    total : number;
    rows : unknown[][];
    warnings : string[];
}

export interface IScaleValue {
    code : string;
    label : string;
    // Forbidden is a hard no, Penalty costs weight, Numeric is a number typed in
    meaning : 'Forbidden' | 'Allowed' | 'Penalty' | 'Numeric';
    weight : number;
    colour : string | null;
}
export interface IValueScale {
    id : string;
    profileId : string | null;
    name : string;
    values : IScaleValue[];
}
export interface IRelationship {
    id : string;
    profileId : string;
    name : string;
    entityTypeAId : string;
    entityA : string;
    entityTypeBId : string;
    entityB : string;
    valueScaleId : string;
    valueScaleName : string;
    defaultValue : string | null;
    symmetric : boolean;
    editor : 'Matrix' | 'List';
    drives : string | null;
    sourceId : string | null;
    sourceName : string | null;
    sourceAColumn : string | null;
    sourceBColumn : string | null;
    sourceValueColumn : string | null;
    valueCount : number;
}
export interface IRelationshipDetails {
    name : string;
    entityTypeAId : string;
    entityTypeBId : string;
    valueScaleId : string;
    defaultValue : string | null;
    symmetric : boolean;
    editor : 'Matrix' | 'List';
    drives : string | null;
    sourceId : string | null;
    sourceAColumn : string | null;
    sourceBColumn : string | null;
    sourceValueColumn : string | null;
}
export interface IKeyLabel {
    key : string;
    label : string;
}
// A pair's value, typed is true when it was typed in rather than read from the source
export interface IGridValue {
    a : string;
    b : string;
    value : string;
    typed : boolean;
}
export interface IGrid {
    relationship : IRelationship;
    a : IKeyLabel[];
    b : IKeyLabel[];
    values : IGridValue[];
    warnings : string[];
}
// A blank value clears a typed in pair back to the source or default
export interface IPairValue {
    aKey : string;
    bKey : string;
    value : string;
}

export interface IShift {
    // 0 Sunday to 6 Saturday, an end at or before the start runs past midnight
    dayOfWeek : number;
    startTime : string;
    endTime : string;
}
export interface ICalendarException {
    id? : string | null;
    startTime : string;
    endTime : string;
    kind : 'Down' | 'Available';
    reason : string | null;
}
export interface ICalendar {
    id : string;
    profileId : string;
    name : string;
    parentCalendarId : string | null;
    parentName : string | null;
    resourceEntityTypeId : string | null;
    resourceEntity : string | null;
    resourceKey : string | null;
    shifts : IShift[];
    exceptions : ICalendarException[];
}
export interface ICalendarDetails {
    name : string;
    parentCalendarId : string | null;
    resourceEntityTypeId : string | null;
    resourceKey : string | null;
    shifts : IShift[];
    exceptions : ICalendarException[];
}

// who picks the items a rule is for, params are its block's parameters as text
export interface IRuleBlocks {
    who? : { entity? : string | null; when? : string | null } | null;
    params : {[name : string] : string | null};
}
export interface IRule {
    id : string;
    profileId : string;
    name : string;
    ruleTypeId : string;
    ruleTypeName : string;
    category : string;
    enabled : boolean;
    strength : 'Hard' | 'Soft';
    weight : number;
    blocks : IRuleBlocks;
    sortOrder : number;
}
export interface IRuleDetails {
    name : string;
    ruleTypeId : string;
    enabled : boolean;
    strength : 'Hard' | 'Soft';
    weight : number;
    blocks : IRuleBlocks;
    sortOrder : number | null;
}
export interface IDiagnostic {
    level : 'error' | 'warning' | 'info';
    ruleId : string | null;
    ruleName : string | null;
    itemKey : string | null;
    message : string;
}
// What the rules come to without solving, how many items each touches
export interface IRuleCheck {
    itemEntity : string | null;
    resourceEntity : string | null;
    toolingEntity : string | null;
    items : number;
    resources : number;
    pools : number;
    modes : number;
    noMode : number;
    separations : number;
    precedences : number;
    diagnostics : IDiagnostic[];
    rules : {[ruleId : string] : { items : number; modesRemoved : number }};
}

export type RunKind = 'Solve' | 'WhatIf' | 'Emergency';
export type RunStatus = 'Pending' | 'Running' | 'Optimal' | 'Feasible' | 'Infeasible' | 'Stopped' | 'Failed' | 'Invalid';
export interface IItemPin {
    itemKey : string;
    resourceKey : string | null;
    start : string | null;
}
export interface IResourceDown {
    resourceKey : string;
    from : string;
    to : string;
    reason : string | null;
}
export interface ISolveRequest {
    kind : RunKind;
    horizonStart? : string | null;
    horizonDays? : number | null;
    timeLimitSeconds? : number | null;
    allowUnscheduled? : boolean | null;
    pins? : IItemPin[];
    downs? : IResourceDown[];
    alternatives? : string[];
    unlock? : string[];
}
export interface IInspection {
    kind : string;
    at : string;
    minutes : number;
}
// One segment of an item on a resource, fixed when it was locked
export interface IAssignment {
    id : string;
    itemKey : string;
    itemLabel : string | null;
    segment : number;
    resourceKey : string;
    poolKey : string | null;
    toolCount : number;
    cycles : number;
    quantity : number;
    startTime : string;
    endTime : string;
    setupMinutes : number;
    dueTime : string | null;
    lateMinutes : number;
    cost : number;
    costReasons : string[] | null;
    inspections : IInspection[] | null;
    attributes : {[name : string] : string | null} | null;
    fixed : boolean;
}
export interface IDownRange {
    start : string;
    end : string;
    reason : string | null;
}
export interface IPlanResource {
    key : string;
    label : string;
    attributes : {[name : string] : string | null};
    capacities : {[dimension : string] : number};
    unavailable : IDownRange[];
    // The combination it's set up in, from a Configurations rule, windows in a row with the same one are joined
    configurations? : IConfigurationRange[];
}
export interface IConfigurationRange {
    start : string;
    end : string;
    key : string;
    label : string;
    ruleName : string | null;
}
export interface IRunDetails {
    itemEntity : string | null;
    resourceEntity : string | null;
    toolingEntity : string | null;
    bucketMinutes : number;
    horizonStart : string;
    horizonEnd : string;
    colourBy : string | null;
    labelBy : string | null;
    resources : IPlanResource[];
    pools : { key : string; label : string; copies : number }[];
    lockedCount : number;
    lockedUntil? : string | null;
    downs : IResourceDown[];
}
export interface IUnscheduled {
    itemKey : string;
    label : string;
    reason : string;
    quantity : number | null;
    due : string | null;
}
export interface IChange {
    itemKey : string;
    label : string | null;
    kind : 'Added' | 'Dropped' | 'Moved' | 'Later' | 'Earlier';
    beforeResource : string | null;
    afterResource : string | null;
    beforeStart : string | null;
    afterStart : string | null;
    beforeEnd : string | null;
    afterEnd : string | null;
    shiftMinutes : number | null;
    beforeLateMinutes : number | null;
    afterLateMinutes : number | null;
}
export interface IChangeSummary {
    hasBaseline : boolean;
    moved? : number;
    retimed? : number;
    added? : number;
    dropped? : number;
    lateBefore? : number;
    lateAfter? : number;
    lateHoursBefore? : number;
    lateHoursAfter? : number;
    unscheduledAfter? : number;
}
export interface IConflict {
    ruleId : string;
    name : string;
}
export interface ISolveRun {
    id : string;
    profileId : string;
    kind : RunKind;
    parentRunId : string | null;
    status : RunStatus;
    requested : string;
    started : string | null;
    finished : string | null;
    timeLimitSeconds : number;
    horizonStart : string;
    horizonEnd : string;
    settings : ISolveRequest | null;
    objective : number | null;
    message : string | null;
    infeasibility : IConflict[] | null;
    published : boolean;
    publishedAt : string | null;
    discarded : boolean;
    details : IRunDetails | null;
    diagnostics : IDiagnostic[] | null;
    unscheduled : IUnscheduled[] | null;
    changes : { summary : IChangeSummary; items : IChange[] } | null;
    stats : {[name : string] : number | null} | null;
    assignmentCount : number;
    assignments? : IAssignment[];
}
export interface IPlanLock {
    id : string;
    kind : 'Window' | 'Item';
    lockedUntil : string | null;
    resourceKey : string | null;
    itemKey : string | null;
    reason : string | null;
    created : string;
}
export interface ILockDetails {
    kind : 'Window' | 'Item';
    lockedUntil : string | null;
    resourceKey : string | null;
    itemKey : string | null;
    reason : string | null;
}
// The profile's run settings, kept with the profile
export interface IProfileSettings {
    horizonDays? : number;
    timeLimitSeconds? : number;
    allowUnscheduled? : boolean;
    colourBy? : string | null;
    labelBy? : string | null;
    // the next hours from now are locked on every run
    lockHours? : number;
    [name : string] : unknown;
}

// The API's endpoints, every call goes through callApi
@Injectable({ providedIn : 'root' })
export class SchedulerApi {
    // Sends a call to the API, its error message is thrown when it fails
    async callApi<T>(path : string, method : string = 'GET', body? : any) : Promise<T> {
        let response : Response;
        try {
            response = await fetch(`${environment.api.url}${path}`, {
                method : method,
                headers : body === undefined ? {} : { 'Content-Type' : 'application/json' },
                body : body === undefined ? undefined : JSON.stringify(body)
            });
        }
        catch(e) {
            throw new Error(`The Scheduler API isn't reachable at ${environment.api.url}`);
        }
        if(!response.ok)
            throw new Error(await response.text() || `The API call failed (${response.status})`);
        return await response.json() as T;
    }

    getStatus() : Promise<IStatus> {
        return this.callApi<IStatus>('/api/status');
    }
    getRuleTypes() : Promise<IRuleType[]> {
        return this.callApi<IRuleType[]>('/api/rule-types');
    }

    getProfiles() : Promise<IProfile[]> {
        return this.callApi<IProfile[]>('/api/profiles');
    }
    // A new profile when there's no id
    saveProfile(profileId : string | null, name : string, description : string | null, timeUnitMinutes : number) : Promise<IProfile> {
        const body = { name : name, description : description, timeUnitMinutes : timeUnitMinutes };
        return profileId
            ? this.callApi<IProfile>(`/api/profiles/${profileId}`, 'PUT', body)
            : this.callApi<IProfile>('/api/profiles', 'POST', body);
    }
    getNavigator(profileId : string) : Promise<INavNode[]> {
        return this.callApi<INavNode[]>(`/api/profiles/${profileId}/navigator`);
    }

    getConnection(connectionId : string) : Promise<IConnection> {
        return this.callApi<IConnection>(`/api/connections/${connectionId}`);
    }
    // A new connection when there's no id
    saveConnection(connectionId : string | null, connection : IConnectionDetails) : Promise<IConnection> {
        return connectionId
            ? this.callApi<IConnection>(`/api/connections/${connectionId}`, 'PUT', connection)
            : this.callApi<IConnection>('/api/connections', 'POST', connection);
    }
    // Tests what's in the wizard without saving it, with a connection id a blank password uses the saved one
    testDetails(connectionId : string | null, connection : IConnectionDetails) : Promise<ITestResult> {
        return this.callApi<ITestResult>('/api/connections/test' + (connectionId ? '?connectionId=' + encodeURIComponent(connectionId) : ''), 'POST', connection);
    }
    // Tests a saved connection, the result is kept on it
    testConnection(connectionId : string) : Promise<IConnection> {
        return this.callApi<IConnection>(`/api/connections/${connectionId}/test`, 'POST');
    }
    getObjects(connectionId : string) : Promise<ISourceObject[]> {
        return this.callApi<ISourceObject[]>(`/api/connections/${connectionId}/objects`);
    }
    getObjectColumns(connectionId : string, schema : string, name : string) : Promise<IColumn[]> {
        return this.callApi<IColumn[]>(`/api/connections/${connectionId}/objects/columns?${this.objectQuery(schema, name)}`);
    }
    getObjectPreview(connectionId : string, schema : string, name : string) : Promise<IPreview> {
        return this.callApi<IPreview>(`/api/connections/${connectionId}/objects/preview?${this.objectQuery(schema, name)}`);
    }

    getSource(profileId : string, sourceId : string) : Promise<ISource> {
        return this.callApi<ISource>(`/api/profiles/${profileId}/sources/${sourceId}`);
    }
    // A new source when there's no id, its columns are read before it's saved
    saveSource(profileId : string, sourceId : string | null, source : ISourceDetails) : Promise<ISource> {
        return sourceId
            ? this.callApi<ISource>(`/api/profiles/${profileId}/sources/${sourceId}`, 'PUT', source)
            : this.callApi<ISource>(`/api/profiles/${profileId}/sources`, 'POST', source);
    }
    // Reads its columns again
    refreshSource(profileId : string, sourceId : string) : Promise<ISource> {
        return this.callApi<ISource>(`/api/profiles/${profileId}/sources/${sourceId}/refresh`, 'POST');
    }
    getSourcePreview(profileId : string, sourceId : string) : Promise<IPreview> {
        return this.callApi<IPreview>(`/api/profiles/${profileId}/sources/${sourceId}/preview`);
    }

    getRuleTypeDetails() : Promise<IRuleTypeDetails[]> {
        return this.callApi<IRuleTypeDetails[]>('/api/rule-types');
    }
    getValueScales(profileId : string | null) : Promise<IValueScale[]> {
        return this.callApi<IValueScale[]>('/api/value-scales' + (profileId ? '?profileId=' + encodeURIComponent(profileId) : ''));
    }
    // A new scale when there's no id
    saveValueScale(profileId : string, valueScaleId : string | null, name : string, values : IScaleValue[]) : Promise<IValueScale> {
        const body = { name : name, values : values };
        return valueScaleId
            ? this.callApi<IValueScale>(`/api/profiles/${profileId}/value-scales/${valueScaleId}`, 'PUT', body)
            : this.callApi<IValueScale>(`/api/profiles/${profileId}/value-scales`, 'POST', body);
    }
    saveProfileSettings(profileId : string, settings : IProfileSettings) : Promise<IProfile> {
        return this.callApi<IProfile>(`/api/profiles/${profileId}/schedule/settings`, 'PUT', settings);
    }

    getEntities(profileId : string) : Promise<IEntity[]> {
        return this.callApi<IEntity[]>(`/api/profiles/${profileId}/entities`);
    }
    getEntity(profileId : string, entityTypeId : string) : Promise<IEntity> {
        return this.callApi<IEntity>(`/api/profiles/${profileId}/entities/${entityTypeId}`);
    }
    // A new entity when there's no id
    saveEntity(profileId : string, entityTypeId : string | null, entity : IEntityDetails) : Promise<IEntity> {
        return entityTypeId
            ? this.callApi<IEntity>(`/api/profiles/${profileId}/entities/${entityTypeId}`, 'PUT', entity)
            : this.callApi<IEntity>(`/api/profiles/${profileId}/entities`, 'POST', entity);
    }
    // Replaces the entity's attributes, ones left out are removed
    saveAttributes(profileId : string, entityTypeId : string, attributes : IAttribute[]) : Promise<IEntity> {
        return this.callApi<IEntity>(`/api/profiles/${profileId}/entities/${entityTypeId}/attributes`, 'PUT', attributes);
    }
    deleteEntity(profileId : string, entityTypeId : string) : Promise<unknown> {
        return this.callApi<unknown>(`/api/profiles/${profileId}/entities/${entityTypeId}`, 'DELETE');
    }
    getEntityRows(profileId : string, entityTypeId : string) : Promise<IEntityRows> {
        return this.callApi<IEntityRows>(`/api/profiles/${profileId}/entities/${entityTypeId}/rows`);
    }

    getRelationships(profileId : string) : Promise<IRelationship[]> {
        return this.callApi<IRelationship[]>(`/api/profiles/${profileId}/relationships`);
    }
    // A new relationship when there's no id
    saveRelationship(profileId : string, relationshipId : string | null, relationship : IRelationshipDetails) : Promise<IRelationship> {
        return relationshipId
            ? this.callApi<IRelationship>(`/api/profiles/${profileId}/relationships/${relationshipId}`, 'PUT', relationship)
            : this.callApi<IRelationship>(`/api/profiles/${profileId}/relationships`, 'POST', relationship);
    }
    deleteRelationship(profileId : string, relationshipId : string) : Promise<unknown> {
        return this.callApi<unknown>(`/api/profiles/${profileId}/relationships/${relationshipId}`, 'DELETE');
    }
    getGrid(profileId : string, relationshipId : string) : Promise<IGrid> {
        return this.callApi<IGrid>(`/api/profiles/${profileId}/relationships/${relationshipId}/grid`);
    }
    // Sets typed in values, replace clears the ones not sent
    saveValues(profileId : string, relationshipId : string, values : IPairValue[], replace : boolean = false) : Promise<IRelationship> {
        return this.callApi<IRelationship>(`/api/profiles/${profileId}/relationships/${relationshipId}/values`, 'PUT', { values : values, replace : replace });
    }
    // Copies the source's values in as typed in values
    importValues(profileId : string, relationshipId : string) : Promise<IRelationship> {
        return this.callApi<IRelationship>(`/api/profiles/${profileId}/relationships/${relationshipId}/import`, 'POST');
    }

    getCalendars(profileId : string) : Promise<ICalendar[]> {
        return this.callApi<ICalendar[]>(`/api/profiles/${profileId}/calendars`);
    }
    // A new calendar when there's no id
    saveCalendar(profileId : string, calendarId : string | null, calendar : ICalendarDetails) : Promise<ICalendar> {
        return calendarId
            ? this.callApi<ICalendar>(`/api/profiles/${profileId}/calendars/${calendarId}`, 'PUT', calendar)
            : this.callApi<ICalendar>(`/api/profiles/${profileId}/calendars`, 'POST', calendar);
    }
    deleteCalendar(profileId : string, calendarId : string) : Promise<unknown> {
        return this.callApi<unknown>(`/api/profiles/${profileId}/calendars/${calendarId}`, 'DELETE');
    }

    getRules(profileId : string) : Promise<IRule[]> {
        return this.callApi<IRule[]>(`/api/profiles/${profileId}/rules`);
    }
    // A new rule when there's no id
    saveRule(profileId : string, ruleId : string | null, rule : IRuleDetails) : Promise<IRule> {
        return ruleId
            ? this.callApi<IRule>(`/api/profiles/${profileId}/rules/${ruleId}`, 'PUT', rule)
            : this.callApi<IRule>(`/api/profiles/${profileId}/rules`, 'POST', rule);
    }
    deleteRule(profileId : string, ruleId : string) : Promise<unknown> {
        return this.callApi<unknown>(`/api/profiles/${profileId}/rules/${ruleId}`, 'DELETE');
    }
    checkRules(profileId : string) : Promise<IRuleCheck> {
        return this.callApi<IRuleCheck>(`/api/profiles/${profileId}/rules/check`, 'POST');
    }

    // Starts a run in the background, follow it with getRun
    solve(profileId : string, request : ISolveRequest) : Promise<ISolveRun> {
        return this.callApi<ISolveRun>(`/api/profiles/${profileId}/schedule/solve`, 'POST', request);
    }
    getRuns(profileId : string) : Promise<ISolveRun[]> {
        return this.callApi<ISolveRun[]>(`/api/profiles/${profileId}/schedule/runs`);
    }
    // With its assignments
    getRun(profileId : string, runId : string) : Promise<ISolveRun> {
        return this.callApi<ISolveRun>(`/api/profiles/${profileId}/schedule/runs/${runId}`);
    }
    // The published plan, an empty object when nothing has been published
    getPlan(profileId : string) : Promise<ISolveRun | {}> {
        return this.callApi<ISolveRun | {}>(`/api/profiles/${profileId}/schedule/plan`);
    }
    stopRun(profileId : string, runId : string) : Promise<unknown> {
        return this.callApi<unknown>(`/api/profiles/${profileId}/schedule/runs/${runId}/stop`, 'POST');
    }
    // Makes the run the plan, the one before stops being published
    publishRun(profileId : string, runId : string) : Promise<ISolveRun> {
        return this.callApi<ISolveRun>(`/api/profiles/${profileId}/schedule/runs/${runId}/publish`, 'POST');
    }
    discardRun(profileId : string, runId : string) : Promise<unknown> {
        return this.callApi<unknown>(`/api/profiles/${profileId}/schedule/runs/${runId}/discard`, 'POST');
    }
    getLocks(profileId : string) : Promise<IPlanLock[]> {
        return this.callApi<IPlanLock[]>(`/api/profiles/${profileId}/schedule/locks`);
    }
    addLock(profileId : string, planLock : ILockDetails) : Promise<IPlanLock[]> {
        return this.callApi<IPlanLock[]>(`/api/profiles/${profileId}/schedule/locks`, 'POST', planLock);
    }
    deleteLock(profileId : string, lockId : string) : Promise<unknown> {
        return this.callApi<unknown>(`/api/profiles/${profileId}/schedule/locks/${lockId}`, 'DELETE');
    }

    private objectQuery(schema : string, name : string) : string {
        return `schema=${encodeURIComponent(schema)}&name=${encodeURIComponent(name)}`;
    }
}

// An error's message, for showing on screen
export function getErrorMessage(e : unknown) : string {
    return e instanceof Error ? e.message : String(e);
}
