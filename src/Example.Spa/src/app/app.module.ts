import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { FormsModule } from '@angular/forms';
import {
  HTTP_INTERCEPTORS,
  HttpClient,
  provideHttpClient,
  withInterceptorsFromDi,
  withXhr
} from "@angular/common/http";

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { SandboxComponent } from './components/sandbox/sandbox.component';
import { NetworkDiagramComponent } from './components/network-diagram/network-diagram.component';

import { SandboxService } from './services/sandbox.service';
import { FlowService } from './services/flow.service';
import { ReplaceLineBreaksPipe } from './pipes/replace-line-breaks.pipe';
import { ScenarioDescriptionPipe } from './pipes/scenario-description.pipe';



@NgModule({
    declarations: [
        AppComponent,
        SandboxComponent,
        NetworkDiagramComponent,
        ReplaceLineBreaksPipe,
        ScenarioDescriptionPipe
    ],
    imports: [
        BrowserModule,
        BrowserAnimationsModule,
        FormsModule,
        AppRoutingModule
    ],
    providers: [
        SandboxService,
        FlowService,
        ReplaceLineBreaksPipe,
        provideHttpClient(withXhr(), withInterceptorsFromDi()),
    ],
    bootstrap: [AppComponent]
})
export class AppModule { }
