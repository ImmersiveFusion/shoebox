import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { SandboxComponent } from './components/sandbox/sandbox.component';

const routes: Routes = [
  {
    path: '',
    redirectTo: 'sandbox',
    pathMatch: 'full'
  },
  { path: 'sandbox', component: SandboxComponent },
  { path: 'sandbox/:sandboxId', component: SandboxComponent }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
