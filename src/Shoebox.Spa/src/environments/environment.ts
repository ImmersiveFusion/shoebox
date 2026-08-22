export const environment = {
    apiUri: `${window.location.protocol}//${window.location.hostname}`,
    gitHubUrl: 'https://github.com/ImmersiveFusion/opentelemetry-chaos-sim',
    visualize: (sandboxId: string) => { 
        //alert('No visualizer configured')

        const facets = [
            {
                key:'Tag: sandbox.id',
                option:sandboxId,
                selected: true,
                filter: `Tags/any(t: t eq 'sandbox.id:${sandboxId}')`
        }];

        let url ='';
        switch(window.location.host)
        {
            case 'app01-dev12-if-east-us.azurewebsites.net':
                url = 'https://app01-dev05-if-east-us.azurewebsites.net/apm/c39056c8-f40a-4cea-bf3e-e97a0f6b27f6/2075ff0f-2faa-4995-aa06-76648030f440/traces?lastXMinutes=15&queries=&facets=' + btoa(JSON.stringify(facets));
                break;
            default:
                url = 'https://azure.deepcube.ai/apm/c39056c8-f40a-4cea-bf3e-e97a0f6b27f6/2075ff0f-2faa-4995-aa06-76648030f440/traces?lastXMinutes=15&queries=&facets=' + btoa(JSON.stringify(facets));
                break;
        }

        
        window.open(url);

    },
    subscriptionUrl: 'https://immersivefusion.com/pricing',
    requiresAccountToVisualize: true,
    discordUrl: 'https://discord.gg/bjqnPu8PEX'
};
