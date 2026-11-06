using System.Text.Json;
using SaaS.Build;

try
{
    if (args.Length != 2) throw new InvalidDataException("Usage: SaaS.Build architecture|hygiene|locks|locks-write|evidence <directory>");
    var directory = Path.GetFullPath(args[1]);
    switch (args[0])
    {
        case "architecture":
            var nodes = RepositoryGraph.Load(directory);
            var errors = DependencyRules.Validate(nodes);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            Console.WriteLine($"PASS: {nodes.Count} restored project/package nodes checked.");
            break;
        case "hygiene": ConfigHygiene.ValidateRepository(directory); Console.WriteLine("PASS: tracked secret/config hygiene."); break;
        case "locks": LockInventory.Validate(directory); Console.WriteLine("PASS: exact central pins and lock inventory."); break;
        case "locks-write": LockInventory.Write(directory); break;
        case "evidence":
            Console.WriteLine(JsonSerializer.Serialize(EvidenceReport.Read(directory, ["SaaS.UnitTests", "SaaS.ArchitectureTests"])));
            break;
        default: throw new InvalidDataException("Unknown check command.");
    }
    return 0;
}
catch (Exception error) when (error is InvalidDataException or IOException or JsonException or System.Xml.XmlException or InvalidOperationException)
{
    Console.Error.WriteLine("FAIL: " + error.Message);
    return 1;
}
