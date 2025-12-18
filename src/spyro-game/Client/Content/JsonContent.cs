using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;

namespace SpyroGame.Client.Content;

internal static class JsonContent
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static JsonContent()
    {
        Options.Converters.Add(new Vector3JsonConverter());
    }

    public static T LoadFromResources<T>(string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var json = File.ReadAllText(fullPath);
        var result = JsonSerializer.Deserialize<T>(json, Options);
        return result ?? throw new InvalidOperationException($"Failed to deserialize JSON: {relativePath}");
    }
}

internal sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected object for Vector3.");
        }

        float x = 0;
        float y = 0;
        float z = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new Vector3(x, y, z);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name for Vector3.");
            }

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "x": x = (float)reader.GetDouble(); break;
                case "y": y = (float)reader.GetDouble(); break;
                case "z": z = (float)reader.GetDouble(); break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Unexpected end when reading Vector3.");
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("z", value.Z);
        writer.WriteEndObject();
    }
}
