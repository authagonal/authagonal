using System.Text.Json.Serialization;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<OrganizationDomain>))]
internal partial class AzureJsonContext : JsonSerializerContext
{
}
