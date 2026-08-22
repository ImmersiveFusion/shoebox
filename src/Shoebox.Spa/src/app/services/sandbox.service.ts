import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http'
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class SandboxService {
  constructor(private httpClient: HttpClient) { }

  get() : Observable<any> {
    return this.httpClient.post<any>(`${environment.apiUri}/sandbox`, {});
  }

}
