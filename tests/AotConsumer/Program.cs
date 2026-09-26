using System.Text;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Client;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Views;

var scope = WireJson.Parse(Encoding.UTF8.GetBytes("{\"applicationScopeId\":\"app\",\"endUserId\":\"user\",\"authorizationRevision\":\"9223372036854775807\"}"));
WireJson.ValidateNamed("Scope", scope);
using var view = new SessionView();
view.AppendUserMessage("AOT 原生消费 😀");
using var client = new TansrClient(new TansrClientOptions
{
    BaseUri = new Uri("https://example.invalid"), TokenProvider = _ => Task.FromResult("synthetic-no-request"),
    PrincipalProvider = () => "synthetic-package-principal", ExecutionScopeProvider = () => scope,
});
var preview = new TerminalSessionControl(client, enablePreview: true);
var change = WireJson.Parse(Encoding.UTF8.GetBytes("{\"thinking\":{\"budget\":2048}}"));
var operation = preview.CreateConfigurationOperation("session", "request", 0, change);
if (operation.Attempted || !preview.RestoreConfigurationOperation(operation.Request, operation.Scope).Attempted) throw new InvalidOperationException("preview_restore");
Console.WriteLine("AOT_CONSUMER_OK " + WireJson.CanonicalString(scope));
