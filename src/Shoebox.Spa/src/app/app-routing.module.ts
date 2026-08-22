import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { ShoeboxComponent } from './shoebox/shoebox.component';

const routes: Routes = [
  { path: '', component: ShoeboxComponent },
  { path: '**', redirectTo: '' },
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule],
})
export class AppRoutingModule { }
