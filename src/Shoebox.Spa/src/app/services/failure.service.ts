import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';
import { Observable } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class FailureService {

  constructor(private httpClient: HttpClient) { }

  status(resource: string, sandboxId: string) : Observable<any> {
    return this.httpClient.get<any>(`${environment.apiUri}/failure/${resource}/status?sandboxId=${sandboxId}`);
   }
  
  inject(resource: string, sandboxId: string)  : Observable<any> {
   return this.httpClient.post<any>(`${environment.apiUri}/failure/${resource}/inject?sandboxId=${sandboxId}`, {});
  }
  eject(resource: string, sandboxId: string)  : Observable<any> {
    return this.httpClient.post<any>(`${environment.apiUri}/failure/${resource}/eject?sandboxId=${sandboxId}`, {});
  }
}
