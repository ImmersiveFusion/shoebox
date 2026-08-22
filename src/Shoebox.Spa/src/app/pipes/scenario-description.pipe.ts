import { Pipe, PipeTransform } from '@angular/core';
import { FlowScenario } from '../services/flow.service';

@Pipe({
  name: 'scenarioDescription',
  standalone: false
})
export class ScenarioDescriptionPipe implements PipeTransform {
  transform(scenarios: FlowScenario[], selectedId: string): string {
    const scenario = scenarios.find(s => s.id === selectedId);
    return scenario?.description || '';
  }
}
