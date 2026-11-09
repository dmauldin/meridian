using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Net.Client;

namespace Meridian.Server.Auth;

/// <summary>Source-generated JSON for the slim (trimming-friendly) host.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(string))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
