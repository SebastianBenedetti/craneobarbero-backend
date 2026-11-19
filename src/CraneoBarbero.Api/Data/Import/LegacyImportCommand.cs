namespace CraneoBarbero.Api.Data.Import;

public static class LegacyImportCommand
{
    public const string Name = "import-legacy";

    public static async Task<int> RunAsync(IServiceProvider services, string[] arguments)
    {
        if (arguments.Length != 1 || !File.Exists(arguments[0]))
        {
            Console.Error.WriteLine($"Uso: dotnet run --project src/CraneoBarbero.Api -- {Name} <ruta del respaldo .sql o .sql.gz>");
            return 2;
        }

        try
        {
            var dump = await PostgresDump.ReadFileAsync(arguments[0], CancellationToken.None);
            using var scope = services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<LegacyImporter>().ImportAsync(dump, CancellationToken.None);

            Console.WriteLine("Importación terminada.");
            Console.WriteLine($"  Sucursales: {result.Branches}");
            Console.WriteLine($"  Servicios: {result.Cuts}");
            Console.WriteLine($"  Usuarios: {result.Users} ({Describe(result.UsersByRole)})");
            Console.WriteLine($"  Turnos: {result.Appointments} ({Describe(result.AppointmentsByStatus)})");
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"No se importó nada: {exception.Message}");
            return 1;
        }
    }

    private static string Describe(IReadOnlyDictionary<string, int> counts) =>
        string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value}"));
}
