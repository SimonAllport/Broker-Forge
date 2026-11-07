namespace BrokerForge.Host.Config;

/// <summary>
/// Reads the interim file configuration (D-10, D-16): service instances, OAuth resources and object maps from host
/// settings, descriptors from <c>Config:DescriptorsPath/&lt;instance&gt;/*.yaml</c> and table schemas from
/// <c>Config:SchemasPath/&lt;instance&gt;.yaml</c>. It only reads; <see cref="HostConfigStore"/> validates.
/// </summary>
public static class FileConfigInput
{
    public static ConfigInput Read(IConfiguration configuration)
    {
        var input = new ConfigInput
        {
            ConnectBaseUrl = configuration["Connect:PublicBaseUrl"],
            ConnectClientId = configuration["Connect:ClientId"],
        };
        foreach (var s in configuration.GetSection("OAuthResources").GetChildren())
            input.OAuthResources[s.Key] = s.Get<OAuthResourceOptions>() ?? new OAuthResourceOptions();
        foreach (var s in configuration.GetSection("Instances").GetChildren())
            input.Instances[s.Key] = s.Get<InstanceOptions>() ?? new InstanceOptions();
        foreach (var s in configuration.GetSection("ObjectMaps").GetChildren())
            input.ObjectMaps[s.Key] = s.Get<ObjectMapOptions>() ?? new ObjectMapOptions();
        ReadSchemas(configuration["Config:SchemasPath"], input);
        ReadDescriptors(configuration["Config:DescriptorsPath"], input);
        return input;
    }

    private static void ReadSchemas(string? root, ConfigInput input)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;
        if (!Directory.Exists(root))
        {
            input.Problems.Add($"Config:SchemasPath `{root}` is not a directory");
            return;
        }
        foreach (var instance in input.Instances.Keys)
        {
            var file = Path.Combine(root, instance + ".yaml");
            if (File.Exists(file))
                input.SchemaTexts[instance] = File.ReadAllText(file);
        }
    }

    /// <summary>No path means no file descriptors, as deployed since every source is a connection design (plan 5).</summary>
    private static void ReadDescriptors(string? root, ConfigInput input)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;
        if (!Directory.Exists(root))
        {
            input.DescriptorProblems.Add($"Config:DescriptorsPath `{root}` is not a directory");
            return;
        }
        foreach (var instance in input.Instances.Keys)
        {
            var dir = Path.Combine(root, instance);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal))
                input.DescriptorTexts[(instance, Path.GetFileName(file))] = File.ReadAllText(file);
        }
    }
}
