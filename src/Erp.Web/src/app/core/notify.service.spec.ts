import { HttpErrorResponse } from '@angular/common/http';
import { errorKey } from './notify.service';

describe('errorKey', () => {
  it('tells a lost connection apart from a server that is down', () => {
    expect(errorKey(new HttpErrorResponse({ status: 0 }))).toBe('app.errorOffline');
    expect(errorKey(new HttpErrorResponse({ status: 502 }))).toBe('app.errorServerDown');
    expect(errorKey(new HttpErrorResponse({ status: 503 }))).toBe('app.errorServerDown');
    expect(errorKey(new HttpErrorResponse({ status: 500 }))).toBe('app.errorTitle');
    expect(errorKey(new Error('x'))).toBe('app.errorTitle');
  });
});
