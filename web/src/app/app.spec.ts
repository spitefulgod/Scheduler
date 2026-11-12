import { TestBed } from '@angular/core/testing';
import { App } from './app';

// A pretend API, the login answers with the user given, everything else with an empty list
function fakeApi(login : object | null) : void {
    globalThis.fetch = ((input : RequestInfo | URL) => {
        const url = String(input);
        if(url.endsWith('/api/user/login'))
            return Promise.resolve(login ? new Response(JSON.stringify(login), { status : 200 }) : new Response('Unauthorized', { status : 401 }));
        return Promise.resolve(new Response('[]', { status : 200 }));
    }) as typeof fetch;
}
// Lets the pretend calls finish
function settle() : Promise<void> {
    return new Promise(resolve => setTimeout(resolve, 50));
}
const SITE_OWNER = { id : null, userName : 'TEST\\owner', displayName : 'Owner', initials : 'OW', email : null, siteOwner : true, designer : true, profiles : [], settings : {} };

describe('App', () => {
    beforeEach(async () => {
        await TestBed.configureTestingModule({ imports : [App] }).compileComponents();
    });

    it('shows the ribbon tabs once signed in', async () => {
        fakeApi(SITE_OWNER);
        const fixture = TestBed.createComponent(App);
        fixture.detectChanges();
        await settle();
        fixture.detectChanges();
        const tabs = [...(fixture.nativeElement as HTMLElement).querySelectorAll('app-ribbon .tab')].map(x => x.textContent?.trim());
        expect(tabs).toEqual(['Home', 'Data', 'Model', 'Setup', 'Rules', 'Schedule', 'View']);
    });

    it('shows the login error page when Windows sign in is refused', async () => {
        fakeApi(null);
        const fixture = TestBed.createComponent(App);
        fixture.detectChanges();
        await settle();
        fixture.detectChanges();
        expect((fixture.nativeElement as HTMLElement).querySelector('.splashCard h2')?.textContent).toBe('Access denied');
    });
});
