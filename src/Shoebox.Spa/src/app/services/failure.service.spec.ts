import { TestBed } from '@angular/core/testing';

import { FailureService } from './failure.service';

describe('FailureService', () => {
  let service: FailureService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(FailureService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
