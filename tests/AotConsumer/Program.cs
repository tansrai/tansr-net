using System.Text;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Views;

var scope = WireJson.Parse(Encoding.UTF8.GetBytes("{\"applicationScopeId\":\"app\",\"endUserId\":\"user\",\"authorizationRevision\":\"9223372036854775807\"}"));
WireJson.ValidateNamed("Scope", scope);
using var view = new SessionView();
view.AppendUserMessage("AOT 原生消费 😀");
Console.WriteLine("AOT_CONSUMER_OK " + WireJson.CanonicalString(scope));
