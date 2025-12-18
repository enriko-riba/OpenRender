namespace SpyroGame.Client.Content.EntityModels;

internal static class EntityModelLoader
{
    public static EntityModelDefinition Load(string modelJsonPath)
        => JsonContent.LoadFromResources<EntityModelDefinition>(modelJsonPath);
}
