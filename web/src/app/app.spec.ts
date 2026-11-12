import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
    beforeEach(async () => {
        // no API in the tests, every call fails straight away
        globalThis.fetch = () => Promise.reject(new Error('No API in tests'));
        await TestBed.configureTestingModule({ imports : [App] }).compileComponents();
    });

    it('shows the ribbon tabs', async () => {
        const fixture = TestBed.createComponent(App);
        await fixture.whenStable();
        const tabs = [...(fixture.nativeElement as HTMLElement).querySelectorAll('app-ribbon .tab')].map(x => x.textContent?.trim());
        expect(tabs).toEqual(['Home', 'Data', 'Model', 'Setup', 'Rules', 'Schedule', 'View']);
    });
});
