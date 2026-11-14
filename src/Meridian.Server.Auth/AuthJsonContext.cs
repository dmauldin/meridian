using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Net.Client;

namespace Meridian.Server.Auth;

/// <summary>Source-generated JSON for the slim (trimming-friendly) host.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Credentials))]
[JsonSerializable(typeof(RegisterResponse))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(CreateCharacterRequest))]
[JsonSerializable(typeof(CharacterSummary))]
[JsonSerializable(typeof(List<CharacterSummary>))]
[JsonSerializable(typeof(PlayRequest))]
[JsonSerializable(typeof(PlayResponse))]
[JsonSerializable(typeof(AuthError))]
[JsonSerializable(typeof(string))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
