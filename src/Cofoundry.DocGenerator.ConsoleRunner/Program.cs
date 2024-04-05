using Microsoft.Extensions.Configuration;

namespace Cofoundry.DocGenerator.ConsoleRunner;

class Program
{
    static async Task Main(string[] args)
    {
        var settings = ParseConfigSettings();
        var docGenerator = new Core.DocGenerator(settings);

        try
        {
            await docGenerator.GenerateAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            throw;
        }
    }

    private static DocGeneratorSettings ParseConfigSettings()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.local.json", true);

        var configuration = builder.Build();

        var settings = configuration.Get<DocGeneratorSettings>();

        if (settings == null)
        {
            throw new Exception($"Could not bind {nameof(DocGeneratorSettings)}");
        }
        return settings;
    }
}
