import { Pipe, PipeTransform } from '@angular/core';
@Pipe({
    name: 'rn2br',
    standalone: false
})
export class ReplaceLineBreaksPipe implements PipeTransform {
transform(value: string): string {
      return value.replace(/\\r\\n/g, '<br/>');
   }
}