using DotNetEnv;

namespace CraneoBarbero.Api.Configuration;

public static class DotEnvLoader
{
    public static void AddDotEnvForDevelopment(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            return;
        }

        var values = Env.NoEnvVars().TraversePath().Load()
            .Where(pair => !string.IsNullOrEmpty(pair.Value) && string.IsNullOrEmpty(builder.Configuration[pair.Key]))
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value);

        builder.Configuration.AddInMemoryCollection(values);
    }
}
